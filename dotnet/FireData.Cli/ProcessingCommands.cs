using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FireData.Cli;

internal static class ProcessingCommands
{
    private const long MicrosecondsPerDay = 86_400_000_000;
    private const double EarthRadiusKm = 6_371.0088;
    private const string AlgorithmVersion = "fire-processing-csharp-v1";

    public static async Task<int> RunAsync(string databasePath, Arguments options)
    {
        var gridDegrees = options.GetDouble("--grid-degrees", 0.25);
        var epsilonMeters = options.GetDouble("--epsilon-m", 2_000);
        var timeScaleMetersPerDay = options.GetDouble("--time-scale-m-per-day", 1_500);
        var minPoints = options.GetInt("--min-points", 4);
        if (gridDegrees is <= 0 or > 30)
        {
            throw new ArgumentException("--grid-degrees must be greater than 0 and no greater than 30.");
        }

        if (epsilonMeters is <= 0 or > 20_000_000)
        {
            throw new ArgumentException("--epsilon-m must be greater than 0 and no greater than 20000000.");
        }

        if (timeScaleMetersPerDay is <= 0 or > 20_000_000)
        {
            throw new ArgumentException(
                "--time-scale-m-per-day must be greater than 0 and no greater than 20000000.");
        }

        if (minPoints < 1)
        {
            throw new ArgumentException("--min-points must be at least one.");
        }

        var timeWindowDays = epsilonMeters / timeScaleMetersPerDay;
        if (timeWindowDays > long.MaxValue / (double)MicrosecondsPerDay)
        {
            throw new ArgumentException("The selected time scale produces an unbounded neighborhood window.");
        }

        var instrument = options.GetInstrument();
        var confidenceClass = options.GetConfidenceClass();
        var from = options.GetUtcMicroseconds("--from");
        var to = options.GetUtcMicroseconds("--to");
        if (from.HasValue && to.HasValue && from.Value >= to.Value)
        {
            throw new ArgumentException("--from must be earlier than --to.");
        }

        var gridScheme = $"latlon-{gridDegrees.ToString("0.########", CultureInfo.InvariantCulture)}-degrees-v1";
        var totalStopwatch = Stopwatch.StartNew();
        var stagingStopwatch = Stopwatch.StartNew();
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Foreign Keys=True");
        await connection.OpenAsync();
        await using (var tempStore = connection.CreateCommand())
        {
            tempStore.CommandText = "PRAGMA temp_store = FILE";
            await tempStore.ExecuteNonQueryAsync();
        }

        await using var transaction = connection.BeginTransaction();
        await CreateWorkingTablesAsync(connection, transaction);

        var (selectionSql, selectionParameters) = BuildSelection(
            instrument,
            confidenceClass,
            from,
            to);
        var (inputCount, cleanedCount) = await CountInputRowsAsync(
            connection,
            transaction,
            instrument,
            confidenceClass,
            from,
            to);
        if (inputCount == 0)
        {
            throw new InvalidOperationException("No observations match the selected processing input.");
        }

        var qualitySql = "scan > 0 AND scan <= 1.0e308 " +
            "AND track > 0 AND track <= 1.0e308 " +
            "AND brightness BETWEEN -1.0e308 AND 1.0e308 " +
            "AND brightness_t31 BETWEEN -1.0e308 AND 1.0e308 " +
            "AND fire_radiative_power BETWEEN 0 AND 1.0e308 " +
            "AND confidence_class IS NOT NULL";
        var cleanedSql = $"{selectionSql} AND {qualitySql}";
        if (cleanedCount == 0)
        {
            throw new InvalidOperationException(
                "The selected input contains no observations that pass the processing quality rules.");
        }

        var parametersJson = JsonSerializer.Serialize(new
        {
            instrument = instrument ?? "all",
            confidenceClass,
            fromUtcUs = from,
            toUtcUs = to,
            qualityRules = new[]
            {
                "scan and track must be positive finite values",
                "brightness and brightness_t31 must be finite",
                "fire_radiative_power must be finite and non-negative",
                "confidence_class must be present"
            },
            gridDegrees,
            gridScheme,
            epsilonMeters,
            timeScaleMetersPerDay,
            minPoints,
            spatiotemporalMetric =
                "sqrt(haversine_distance_m^2 + (time_delta_days * time_scale_m_per_day)^2)",
            timeWindowDays,
            presenceHeuristic = "same-or-adjacent-detection-cell-v1"
        });
        var runId = await CreateProcessingRunAsync(
            connection,
            transaction,
            "clean-cluster-aggregate",
            parametersJson,
            JsonSerializer.Serialize(new
            {
                observationsTable = "observations",
                provenanceTable = "processing_run_sources",
                eventsTable = "processing_events",
                dailyMetricsTable = "daily_cell_metrics",
                presenceTable = "daily_cell_presence_heuristic"
            }));

        await PersistSourceProvenanceAsync(
            connection,
            transaction,
            runId,
            selectionSql,
            qualitySql,
            selectionParameters);
        var gridWidth = (long)Math.Ceiling(360.0 / gridDegrees);
        var gridHeight = (long)Math.Ceiling(180.0 / gridDegrees);
        await PopulateWorkingPointsAsync(
            connection,
            transaction,
            cleanedSql,
            selectionParameters,
            gridDegrees,
            gridWidth,
            gridHeight);
        var stagedCount = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM temp.processing_points");
        if (stagedCount != cleanedCount)
        {
            throw new InvalidOperationException(
                $"EF Core selected {cleanedCount} clean observations, but SQLite staged {stagedCount}.");
        }

        await CreateWorkingIndexesAsync(connection, transaction);
        Console.Error.WriteLine($"Input selection and staging completed in {stagingStopwatch.Elapsed:g}.");

        var clusteringStopwatch = Stopwatch.StartNew();
        var instrumentNames = new List<string>();
        await using (var command = CreateCommand(
            connection,
            transaction,
            "SELECT DISTINCT instrument FROM temp.processing_points ORDER BY instrument"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                instrumentNames.Add(reader.GetString(0));
            }
        }

        var totalClusters = 0;
        foreach (var instrumentName in instrumentNames)
        {
            var clusters = await ClusterInstrumentAsync(
                connection,
                transaction,
                instrumentName,
                epsilonMeters,
                timeScaleMetersPerDay,
                minPoints);
            totalClusters += clusters;
        }

        Console.Error.WriteLine($"Clustering completed in {clusteringStopwatch.Elapsed:g}.");
        var totalNoise = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM temp.processing_points WHERE cluster_id = -1");
        var persistenceStopwatch = Stopwatch.StartNew();
        await PersistMembershipsAsync(connection, transaction, runId);
        await PersistEventsAsync(connection, transaction, runId);
        await PersistDailyMetricsAsync(
            connection,
            transaction,
            runId,
            gridDegrees,
            gridScheme);
        await PersistPresenceHeuristicAsync(
            connection,
            transaction,
            runId,
            gridWidth,
            gridHeight,
            gridScheme);

        await transaction.CommitAsync();
        Console.Error.WriteLine(
            $"Aggregation and persistence completed in {persistenceStopwatch.Elapsed:g}.");
        Console.Error.WriteLine($"Processing completed in {totalStopwatch.Elapsed:g}.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            preprocessingRunId = runId,
            selectedObservations = inputCount,
            processedObservations = cleanedCount,
            excludedByQualityRules = inputCount - cleanedCount,
            clusters = totalClusters,
            noiseObservations = totalNoise,
            gridScheme,
            presenceHeuristic = "same-or-adjacent-detection-cell-v1"
        }));
        return 0;
    }

    public static Task<int> CalibrateAsync(string databasePath, Arguments options) =>
        CalibrationCommands.RunAsync(databasePath, options);

    private static async Task CreateWorkingTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TEMP TABLE processing_points (
                observation_id INTEGER PRIMARY KEY,
                instrument TEXT NOT NULL,
                acquired_at_utc_us INTEGER NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                frp REAL NOT NULL,
                utc_date TEXT NOT NULL,
                cell_x INTEGER NOT NULL,
                cell_y INTEGER NOT NULL,
                visited INTEGER NOT NULL DEFAULT 0,
                cluster_id INTEGER NOT NULL DEFAULT 0,
                is_core_point INTEGER NOT NULL DEFAULT 0
            );
            CREATE TEMP TABLE processing_queue (
                queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
                observation_id INTEGER NOT NULL UNIQUE,
                processed INTEGER NOT NULL DEFAULT 0
            );
            CREATE TEMP TABLE processing_neighbors (
                observation_id INTEGER PRIMARY KEY
            );
            CREATE TEMP TABLE processing_core_points (
                observation_id INTEGER PRIMARY KEY
            );
            CREATE TEMP TABLE processing_frontier (
                queue_id INTEGER PRIMARY KEY,
                observation_id INTEGER NOT NULL,
                acquired_at_utc_us INTEGER NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                was_visited INTEGER NOT NULL,
                previous_cluster_id INTEGER NOT NULL
            );
            """);
    }

    private static async Task CreateWorkingIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE INDEX temp.ix_processing_points_instrument_id
                ON processing_points (instrument, observation_id);
            CREATE INDEX temp.ix_processing_points_grid
                ON processing_points (instrument, utc_date, cell_x, cell_y);
            """);
    }

    private static (string Sql, List<SqliteParameter> Parameters) BuildSelection(
        string? instrument,
        string? confidenceClass,
        long? from,
        long? to)
    {
        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (instrument is not null)
        {
            clauses.Add("instrument = $instrument");
            parameters.Add(new SqliteParameter("$instrument", instrument));
        }

        if (confidenceClass is not null)
        {
            clauses.Add("confidence_class = $confidence");
            parameters.Add(new SqliteParameter("$confidence", confidenceClass));
        }

        if (from.HasValue)
        {
            clauses.Add("acquired_at_utc_us >= $from");
            parameters.Add(new SqliteParameter("$from", from.Value));
        }

        if (to.HasValue)
        {
            clauses.Add("acquired_at_utc_us < $to");
            parameters.Add(new SqliteParameter("$to", to.Value));
        }

        return (clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses), parameters);
    }

    private static async Task<(long Selected, long Cleaned)> CountInputRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? instrument,
        string? confidenceClass,
        long? from,
        long? to)
    {
        var contextOptions = new DbContextOptionsBuilder<FireDataContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new FireDataContext(contextOptions);
        context.Database.UseTransaction(transaction);

        IQueryable<Observation> selected = context.Observations.AsNoTracking();
        if (instrument is not null)
        {
            selected = selected.Where(x => x.Instrument == instrument);
        }

        if (confidenceClass is not null)
        {
            selected = selected.Where(x => x.ConfidenceClass == confidenceClass);
        }

        if (from.HasValue)
        {
            selected = selected.Where(x => x.AcquiredAtUtcUs >= from.Value);
        }

        if (to.HasValue)
        {
            selected = selected.Where(x => x.AcquiredAtUtcUs < to.Value);
        }

        var selectedCount = await selected.LongCountAsync();
        var cleanedCount = await selected
            .Where(x =>
                x.Scan > 0 && x.Scan <= 1.0e308 &&
                x.Track > 0 && x.Track <= 1.0e308 &&
                x.Brightness >= -1.0e308 && x.Brightness <= 1.0e308 &&
                x.BrightnessT31 >= -1.0e308 && x.BrightnessT31 <= 1.0e308 &&
                x.FireRadiativePower >= 0 && x.FireRadiativePower <= 1.0e308 &&
                x.ConfidenceClass != null)
            .LongCountAsync();
        return (selectedCount, cleanedCount);
    }

    private static async Task PopulateWorkingPointsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string where,
        IReadOnlyCollection<SqliteParameter> parameters,
        double gridDegrees,
        long gridWidth,
        long gridHeight)
    {
        var command = CreateCommand(
            connection,
            transaction,
            $"""
            INSERT INTO temp.processing_points (
                observation_id, instrument, acquired_at_utc_us, latitude, longitude,
                frp, utc_date, cell_x, cell_y
            )
            SELECT observation_id, instrument, acquired_at_utc_us, latitude, longitude,
                   fire_radiative_power,
                   date(acquired_at_utc_us / 1000000.0, 'unixepoch'),
                   min(CAST((longitude + 180.0) / $grid AS INTEGER), $max_x),
                   min(CAST((latitude + 90.0) / $grid AS INTEGER), $max_y)
            FROM observations
            WHERE {where}
            """);
        command.Parameters.Add(new SqliteParameter("$grid", gridDegrees));
        command.Parameters.Add(new SqliteParameter("$max_x", gridWidth - 1));
        command.Parameters.Add(new SqliteParameter("$max_y", gridHeight - 1));
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task PersistSourceProvenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        string selectionSql,
        string qualitySql,
        IReadOnlyCollection<SqliteParameter> selectionParameters)
    {
        var command = CreateCommand(
            connection,
            transaction,
            $"""
            INSERT INTO processing_run_sources (
                preprocessing_run_id, source_file_id, source_sha256,
                selected_observation_count, processed_observation_count
            )
            SELECT $run_id, o.source_file_id, s.sha256, COUNT(*),
                   SUM(CASE WHEN {qualitySql} THEN 1 ELSE 0 END)
            FROM observations AS o
            JOIN source_files AS s ON s.source_file_id = o.source_file_id
            WHERE {selectionSql}
            GROUP BY o.source_file_id, s.sha256
            """);
        command.Parameters.AddWithValue("$run_id", runId);
        foreach (var parameter in selectionParameters)
        {
            command.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CreateProcessingRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runKind,
        string parametersJson,
        string outputSchemaJson)
    {
        var createdAt = SpatialMath.ToUnixMicroseconds(DateTimeOffset.UtcNow);
        var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO preprocessing_runs (
                run_kind, algorithm_version, created_at_utc_us, parameters_json, output_schema_json
            ) VALUES ($kind, $version, $created, $parameters, $output);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$kind", runKind);
        command.Parameters.AddWithValue("$version", AlgorithmVersion);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$parameters", parametersJson);
        command.Parameters.AddWithValue("$output", outputSchemaJson);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> ClusterInstrumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrument,
        double epsilonMeters,
        double timeScaleMetersPerDay,
        int minPoints)
        => await DatabaseDbscan.ClusterAsync(
            connection,
            transaction,
            instrument,
            epsilonMeters,
            timeScaleMetersPerDay,
            minPoints);

    private static async Task PersistMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId) =>
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO cluster_memberships (
                preprocessing_run_id, observation_id, cluster_id, is_core_point
            )
            SELECT $run_id, observation_id,
                   CASE WHEN cluster_id > 0 THEN cluster_id ELSE NULL END,
                   is_core_point
            FROM temp.processing_points
            """,
            new SqliteParameter("$run_id", runId));

    private static async Task PersistEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId)
    {
        RegisterMathFunctions(connection);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO processing_events (
                preprocessing_run_id, instrument, cluster_id, observation_count,
                start_at_utc_us, end_at_utc_us, centroid_latitude, centroid_longitude,
                frp_sum, frp_max
            )
            SELECT $run_id, p.instrument, m.cluster_id, COUNT(*),
                   MIN(p.acquired_at_utc_us), MAX(p.acquired_at_utc_us),
                   AVG(p.latitude),
                   atan2(AVG(sin(radians(p.longitude))), AVG(cos(radians(p.longitude))))
                       * 180.0 / pi(),
                   SUM(p.frp), MAX(p.frp)
            FROM temp.processing_points AS p
            JOIN cluster_memberships AS m
              ON m.preprocessing_run_id = $run_id
             AND m.observation_id = p.observation_id
            WHERE m.cluster_id IS NOT NULL
            GROUP BY p.instrument, m.cluster_id
            """,
            new SqliteParameter("$run_id", runId));
    }

    private static async Task PersistDailyMetricsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        double gridDegrees,
        string gridScheme)
    {
        connection.CreateFunction("cell_area_km2", (long cellX, long cellY) =>
        {
            var south = Math.Max(-90, -90 + cellY * gridDegrees);
            var north = Math.Min(90, south + gridDegrees);
            var west = -180 + cellX * gridDegrees;
            var width = Math.Min(gridDegrees, 180 - west);
            return EarthRadiusKm * EarthRadiusKm *
                (width * Math.PI / 180) *
                (Math.Sin(north * Math.PI / 180) - Math.Sin(south * Math.PI / 180));
        });
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO daily_cell_metrics (
                preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y, instrument,
                detection_count, event_count, frp_sum, frp_max, cell_area_km2
            )
            SELECT $run_id, p.utc_date, $grid_scheme, p.cell_x, p.cell_y, p.instrument,
                   COUNT(*), COUNT(DISTINCT m.cluster_id), SUM(p.frp), MAX(p.frp),
                   cell_area_km2(p.cell_x, p.cell_y)
            FROM temp.processing_points AS p
            LEFT JOIN cluster_memberships AS m
              ON m.preprocessing_run_id = $run_id
             AND m.observation_id = p.observation_id
            GROUP BY p.utc_date, p.cell_x, p.cell_y, p.instrument
            """,
            new SqliteParameter("$run_id", runId),
            new SqliteParameter("$grid_scheme", gridScheme));
    }

    private static async Task PersistPresenceHeuristicAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        long gridWidth,
        long gridHeight,
        string gridScheme)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            WITH offsets(dx, dy) AS (
                VALUES (-1, -1), (0, -1), (1, -1),
                       (-1, 0),  (0, 0),  (1, 0),
                       (-1, 1),  (0, 1),  (1, 1)
            ),
            candidates AS (
                SELECT d.utc_date, d.instrument,
                       (d.cell_x + offsets.dx + $grid_width) % $grid_width AS cell_x,
                       d.cell_y + offsets.dy AS cell_y,
                       d.cell_x AS source_x, d.cell_y AS source_y
                FROM daily_cell_metrics AS d
                CROSS JOIN offsets
                WHERE d.preprocessing_run_id = $run_id
                  AND d.cell_y + offsets.dy BETWEEN 0 AND $max_y
            )
            INSERT INTO daily_cell_presence_heuristic (
                preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y,
                instrument, has_direct_detection, neighboring_detection_cells, method
            )
            SELECT $run_id, c.utc_date, $grid_scheme, c.cell_x, c.cell_y, c.instrument,
                   MAX(CASE WHEN c.cell_x = c.source_x AND c.cell_y = c.source_y THEN 1 ELSE 0 END),
                   COUNT(*),
                   'same-or-adjacent-detection-cell-v1'
            FROM candidates AS c
            GROUP BY c.utc_date, c.instrument, c.cell_x, c.cell_y
            """,
            new SqliteParameter("$run_id", runId),
            new SqliteParameter("$grid_scheme", gridScheme),
            new SqliteParameter("$grid_width", gridWidth),
            new SqliteParameter("$max_y", gridHeight - 1));
    }

    private static void RegisterMathFunctions(SqliteConnection connection)
    {
        connection.CreateFunction("radians", (double degrees) => degrees * Math.PI / 180.0);
        connection.CreateFunction("sin", (double value) => Math.Sin(value));
        connection.CreateFunction("cos", (double value) => Math.Cos(value));
        connection.CreateFunction("atan2", (double y, double x) => Math.Atan2(y, x));
        connection.CreateFunction("pi", () => Math.PI);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(
                    new SqliteParameter(parameter.ParameterName, parameter.Value));
            }
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params SqliteParameter[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

}
