using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FireData.Cli;

internal static class Commands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var options = Arguments.Parse(args[1..]);
            var databasePath = Path.GetFullPath(options.Get("--database", "data/fire-observations.sqlite"));

            if (command == "init")
            {
                await InitializeAsync(databasePath);
                Console.WriteLine($"Initialized {databasePath}");
                return 0;
            }

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    $"Database not found: {databasePath}. Import data with the Python sqlite_import module first.");
            }

            await using var context = CreateContext(databasePath);
            await ValidateDatabaseAsync(context);

            return command switch
            {
                "query" => await QueryAsync(context, options),
                "bbox" => await BoundingBoxAsync(context, options),
                "radius" => await RadiusAsync(context, options),
                "stats" => await StatsAsync(context, options),
                "cluster" => await ClusterAsync(context, options),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
        }
        catch (Exception error) when (
            error is ArgumentException or FormatException or InvalidOperationException
                or IOException or SqliteException or DbUpdateException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 2;
        }
    }

    private static FireDataContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<FireDataContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=True")
            .Options;
        return new FireDataContext(options);
    }

    private static async Task InitializeAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"Schema SQL file is missing: {schemaPath}");
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True");
        await connection.OpenAsync();
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (version > 1)
            {
                throw new InvalidOperationException(
                    $"Database schema version {version} is newer than this CLI supports (1).");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(schemaPath);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ValidateDatabaseAsync(FireDataContext context)
    {
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (version != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported or missing database schema version {version}; expected 1.");
        }
    }

    private static async Task<int> QueryAsync(FireDataContext context, Arguments options)
    {
        var query = ApplyFilters(context.Observations.AsNoTracking(), options);
        var order = options.Get("--order-by", "time").ToLowerInvariant();
        IOrderedQueryable<Observation> ordered = order switch
        {
            "time" => query.OrderBy(x => x.AcquiredAtUtcUs).ThenBy(x => x.ObservationId),
            "frp" => query.OrderByDescending(x => x.FireRadiativePower).ThenBy(x => x.ObservationId),
            "latitude" => query.OrderBy(x => x.Latitude).ThenBy(x => x.ObservationId),
            "longitude" => query.OrderBy(x => x.Longitude).ThenBy(x => x.ObservationId),
            _ => throw new ArgumentException("--order-by must be time, frp, latitude, or longitude.")
        };
        var limit = options.GetInt("--limit", 1_000);
        if (limit < 0)
        {
            throw new ArgumentException("--limit must be zero (unlimited) or a positive integer.");
        }

        var result = limit == 0 ? ordered : ordered.Take(limit);
        var count = 0;
        await foreach (var observation in result.AsAsyncEnumerable())
        {
            WriteObservation(observation);
            count++;
        }

        Console.Error.WriteLine($"Returned {count:N0} rows.");
        return 0;
    }

    private static async Task<int> BoundingBoxAsync(FireDataContext context, Arguments options)
    {
        var (west, south, east, north) = ParseBoundingBox(options.Require("--bbox"));
        var envelope = new SpatialEnvelope(
            south,
            north,
            west <= east
                ? [new LongitudeInterval(west, east)]
                : [new LongitudeInterval(west, 180), new LongitudeInterval(-180, east)]);
        return await WriteSpatialCandidatesAsync(
            context,
            options,
            envelope,
            observation =>
                observation.Latitude >= south &&
                observation.Latitude <= north &&
                (west <= east
                    ? observation.Longitude >= west && observation.Longitude <= east
                    : observation.Longitude >= west || observation.Longitude <= east),
            options.GetInt("--limit", 0));
    }

    private static async Task<int> RadiusAsync(FireDataContext context, Arguments options)
    {
        var latitude = options.GetDouble("--lat");
        var longitude = options.GetDouble("--lon");
        var radius = options.GetDouble("--radius-m");
        var envelope = SpatialMath.EnvelopeForRadius(latitude, longitude, radius);
        return await WriteSpatialCandidatesAsync(
            context,
            options,
            envelope,
            observation => SpatialMath.DistanceMeters(
                latitude, longitude, observation.Latitude, observation.Longitude) <= radius,
            options.GetInt("--limit", 0));
    }

    private static async Task<int> WriteSpatialCandidatesAsync(
        FireDataContext context,
        Arguments options,
        SpatialEnvelope envelope,
        Func<Observation, bool> exactPredicate,
        int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentException("--limit must be zero (unlimited) or a positive integer.");
        }

        var (sql, parameters) = BuildRTreeQuery(envelope, options);
        var query = context.Observations
            .FromSqlRaw(sql, parameters.ToArray())
            .AsNoTracking()
            .OrderBy(x => x.AcquiredAtUtcUs)
            .ThenBy(x => x.ObservationId);
        var count = 0;
        await foreach (var observation in query.AsAsyncEnumerable())
        {
            if (!exactPredicate(observation))
            {
                continue;
            }

            WriteObservation(observation);
            count++;
            if (limit != 0 && count >= limit)
            {
                break;
            }
        }

        Console.Error.WriteLine($"Returned {count:N0} rows.");
        return 0;
    }

    private static async Task<int> StatsAsync(FireDataContext context, Arguments options)
    {
        var query = ApplyFilters(context.Observations.AsNoTracking(), options);
        var rows = await query
            .GroupBy(x => x.Instrument)
            .Select(group => new
            {
                Instrument = group.Key,
                Count = group.LongCount(),
                AverageBrightness = group.Average(x => x.Brightness),
                AverageFireRadiativePower = group.Average(x => x.FireRadiativePower),
                MinimumTime = group.Min(x => x.AcquiredAtUtcUs),
                MaximumTime = group.Max(x => x.AcquiredAtUtcUs)
            })
            .OrderBy(x => x.Instrument)
            .ToListAsync();

        foreach (var row in rows)
        {
            Console.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        }

        return 0;
    }

    private static async Task<int> ClusterAsync(FireDataContext context, Arguments options)
    {
        var instrument = options.GetInstrument()
            ?? throw new ArgumentException(
                "DBSCAN requires --instrument MODIS or --instrument VIIRS. Run the sensors separately unless a sensor-aware harmonization method has been defined.");
        var epsilon = options.GetDouble("--epsilon-m");
        var minPoints = options.GetInt("--min-points", -1);
        if (epsilon <= 0)
        {
            throw new ArgumentException("--epsilon-m must be greater than zero.");
        }

        if (minPoints < 1)
        {
            throw new ArgumentException("--min-points must be at least one.");
        }

        var filterOptions = options;
        var query = ApplyFilters(context.Observations.AsNoTracking(), filterOptions)
            .Where(x => x.Instrument == instrument)
            .OrderBy(x => x.ObservationId)
            .Select(x => new SpatialPoint(x.ObservationId, x.Latitude, x.Longitude));
        var points = await query.ToListAsync();
        if (points.Count == 0)
        {
            Console.WriteLine("No observations match the selected DBSCAN input.");
            return 0;
        }

        var indicesByObservationId = new Dictionary<long, int>(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            indicesByObservationId.Add(points[index].ObservationId, index);
        }

        await context.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        using var regionQuery = CreateRegionQuery(connection, instrument, options);
        var labels = new int[points.Count]; // 0 = unassigned, -1 = noise, positive = cluster
        var visited = new bool[points.Count];
        var corePoints = new bool[points.Count];
        var queuedForCluster = new int[points.Count];
        var clusterCount = 0;

        for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            if (visited[pointIndex])
            {
                continue;
            }

            visited[pointIndex] = true;
            var neighbors = await FindNeighborsAsync(
                regionQuery,
                points,
                indicesByObservationId,
                pointIndex,
                epsilon);
            if (neighbors.Count < minPoints)
            {
                labels[pointIndex] = -1;
                continue;
            }

            clusterCount++;
            labels[pointIndex] = clusterCount;
            corePoints[pointIndex] = true;
            var queue = new List<int>(neighbors.Count);
            EnqueueNeighbors(neighbors, pointIndex, clusterCount, queuedForCluster, queue);

            for (var queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                var candidateIndex = queue[queueIndex];
                if (!visited[candidateIndex])
                {
                    visited[candidateIndex] = true;
                    var candidateNeighbors = await FindNeighborsAsync(
                        regionQuery,
                        points,
                        indicesByObservationId,
                        candidateIndex,
                        epsilon);
                    if (candidateNeighbors.Count >= minPoints)
                    {
                        corePoints[candidateIndex] = true;
                        EnqueueNeighbors(
                            candidateNeighbors,
                            candidateIndex,
                            clusterCount,
                            queuedForCluster,
                            queue);
                    }
                }

                if (labels[candidateIndex] <= 0)
                {
                    labels[candidateIndex] = clusterCount;
                }
            }
        }

        var parametersJson = JsonSerializer.Serialize(new
        {
            instrument,
            epsilonMeters = epsilon,
            minPoints,
            confidenceClass = options.GetConfidenceClass(),
            fromUtcUs = options.GetUtcMicroseconds("--from"),
            toUtcUs = options.GetUtcMicroseconds("--to"),
            distanceMetric = "spherical-haversine-mean-earth-radius-meters"
        }, JsonOptions);
        await PersistClustersAsync(
            context,
            connection,
            points,
            labels,
            corePoints,
            parametersJson);

        var noiseCount = labels.Count(label => label == -1);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            instrument,
            observations = points.Count,
            clusters = clusterCount,
            noise = noiseCount
        }, JsonOptions));
        return 0;
    }

    private static void EnqueueNeighbors(
        IReadOnlyList<int> neighbors,
        int center,
        int clusterId,
        int[] queuedForCluster,
        List<int> queue)
    {
        foreach (var neighbor in neighbors)
        {
            if (neighbor == center || queuedForCluster[neighbor] == clusterId)
            {
                continue;
            }

            queuedForCluster[neighbor] = clusterId;
            queue.Add(neighbor);
        }
    }

    private static async Task<List<int>> FindNeighborsAsync(
        SqliteCommand command,
        IReadOnlyList<SpatialPoint> points,
        IReadOnlyDictionary<long, int> indicesByObservationId,
        int pointIndex,
        double epsilonMeters)
    {
        var point = points[pointIndex];
        var envelope = SpatialMath.EnvelopeForRadius(
            point.Latitude, point.Longitude, epsilonMeters);
        var longitudeRanges = envelope.LongitudeIntervals;
        command.Parameters["$south"].Value = envelope.South;
        command.Parameters["$north"].Value = envelope.North;
        command.Parameters["$west0"].Value = longitudeRanges[0].West;
        command.Parameters["$east0"].Value = longitudeRanges[0].East;
        command.Parameters["$hasSecond"].Value = longitudeRanges.Count > 1;
        command.Parameters["$west1"].Value = longitudeRanges.Count > 1
            ? longitudeRanges[1].West
            : longitudeRanges[0].West;
        command.Parameters["$east1"].Value = longitudeRanges.Count > 1
            ? longitudeRanges[1].East
            : longitudeRanges[0].East;

        var neighbors = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var observationId = reader.GetInt64(0);
            if (!indicesByObservationId.TryGetValue(observationId, out var neighborIndex))
            {
                continue;
            }

            var neighbor = points[neighborIndex];
            if (SpatialMath.DistanceMeters(
                    point.Latitude, point.Longitude,
                    neighbor.Latitude, neighbor.Longitude) <= epsilonMeters)
            {
                neighbors.Add(neighborIndex);
            }
        }

        return neighbors;
    }

    private static SqliteCommand CreateRegionQuery(
        SqliteConnection connection,
        string instrument,
        Arguments options)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.observation_id, o.latitude, o.longitude
            FROM observation_rtree AS r
            JOIN observations AS o ON o.observation_id = r.observation_id
            WHERE r.min_latitude <= $north
              AND r.max_latitude >= $south
              AND (
                    (r.min_longitude <= $east0 AND r.max_longitude >= $west0)
                    OR
                    ($hasSecond = 1 AND r.min_longitude <= $east1 AND r.max_longitude >= $west1)
                  )
              AND o.instrument = $instrument
            """;
        command.Parameters.AddWithValue("$north", 0d);
        command.Parameters.AddWithValue("$south", 0d);
        command.Parameters.AddWithValue("$west0", 0d);
        command.Parameters.AddWithValue("$east0", 0d);
        command.Parameters.AddWithValue("$hasSecond", 0);
        command.Parameters.AddWithValue("$west1", 0d);
        command.Parameters.AddWithValue("$east1", 0d);
        command.Parameters.AddWithValue("$instrument", instrument);

        var confidenceClass = options.GetConfidenceClass();
        if (confidenceClass is not null)
        {
            command.CommandText += " AND o.confidence_class = $confidence";
            command.Parameters.AddWithValue("$confidence", confidenceClass);
        }

        var from = options.GetUtcMicroseconds("--from");
        if (from is not null)
        {
            command.CommandText += " AND o.acquired_at_utc_us >= $from";
            command.Parameters.AddWithValue("$from", from.Value);
        }

        var to = options.GetUtcMicroseconds("--to");
        if (to is not null)
        {
            command.CommandText += " AND o.acquired_at_utc_us < $to";
            command.Parameters.AddWithValue("$to", to.Value);
        }

        command.Prepare();
        return command;
    }

    private static async Task PersistClustersAsync(
        FireDataContext context,
        SqliteConnection connection,
        IReadOnlyList<SpatialPoint> points,
        IReadOnlyList<int> labels,
        IReadOnlyList<bool> corePoints,
        string parametersJson)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var run = new PreprocessingRun
        {
            RunKind = "DBSCAN",
            AlgorithmVersion = "1",
            CreatedAtUtcUs = SpatialMath.ToUnixMicroseconds(DateTimeOffset.UtcNow),
            ParametersJson = parametersJson,
            OutputSchemaJson = """{"clusterId":"nullable integer; null is noise","isCorePoint":"boolean"}"""
        };
        context.PreprocessingRuns.Add(run);
        await context.SaveChangesAsync();

        var sqliteTransaction = (SqliteTransaction)transaction.GetDbTransaction();
        const int insertBatchSize = 200;
        for (var start = 0; start < points.Count; start += insertBatchSize)
        {
            var count = Math.Min(insertBatchSize, points.Count - start);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            var values = new string[count];
            for (var offset = 0; offset < count; offset++)
            {
                var suffix = offset.ToString(CultureInfo.InvariantCulture);
                values[offset] = $"($run{suffix}, $observation{suffix}, $cluster{suffix}, $core{suffix})";
                command.Parameters.AddWithValue($"$run{suffix}", run.PreprocessingRunId);
                command.Parameters.AddWithValue(
                    $"$observation{suffix}", points[start + offset].ObservationId);
                command.Parameters.AddWithValue(
                    $"$cluster{suffix}",
                    labels[start + offset] > 0
                        ? labels[start + offset]
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    $"$core{suffix}", corePoints[start + offset] ? 1 : 0);
            }

            command.CommandText =
                """
                INSERT INTO cluster_memberships (
                    preprocessing_run_id, observation_id, cluster_id, is_core_point
                ) VALUES
                """ + string.Join(", ", values);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static IQueryable<Observation> ApplyFilters(
        IQueryable<Observation> query,
        Arguments options)
    {
        var instrument = options.GetInstrument();
        if (instrument is not null)
        {
            query = query.Where(x => x.Instrument == instrument);
        }

        var confidence = options.GetConfidenceClass();
        if (confidence is not null)
        {
            query = query.Where(x => x.ConfidenceClass == confidence);
        }

        var from = options.GetUtcMicroseconds("--from");
        if (from is not null)
        {
            query = query.Where(x => x.AcquiredAtUtcUs >= from.Value);
        }

        var to = options.GetUtcMicroseconds("--to");
        if (to is not null)
        {
            query = query.Where(x => x.AcquiredAtUtcUs < to.Value);
        }

        if (options.Has("--bbox"))
        {
            var (west, south, east, north) = ParseBoundingBox(options.Require("--bbox"));
            query = query.Where(x =>
                x.Latitude >= south && x.Latitude <= north &&
                (west <= east
                    ? x.Longitude >= west && x.Longitude <= east
                    : x.Longitude >= west || x.Longitude <= east));
        }

        return query;
    }

    private static (string Sql, object[] Parameters) BuildRTreeQuery(
        SpatialEnvelope envelope,
        Arguments options)
    {
        var parameters = new List<object>
        {
            new SqliteParameter("$south", envelope.South),
            new SqliteParameter("$north", envelope.North)
        };
        var longitudePredicates = new List<string>();
        for (var index = 0; index < envelope.LongitudeIntervals.Count; index++)
        {
            var interval = envelope.LongitudeIntervals[index];
            var westName = $"$west{index}";
            var eastName = $"$east{index}";
            longitudePredicates.Add(
                $"(r.min_longitude <= {eastName} AND r.max_longitude >= {westName})");
            parameters.Add(new SqliteParameter(westName, interval.West));
            parameters.Add(new SqliteParameter(eastName, interval.East));
        }

        var clauses = new List<string>
        {
            "r.min_latitude <= $north",
            "r.max_latitude >= $south",
            $"({string.Join(" OR ", longitudePredicates)})"
        };
        AddSpatialFilter(
            clauses,
            parameters,
            "o.instrument",
            "$instrument",
            options.GetInstrument());
        AddSpatialFilter(
            clauses,
            parameters,
            "o.confidence_class",
            "$confidence",
            options.GetConfidenceClass());

        var from = options.GetUtcMicroseconds("--from");
        if (from is not null)
        {
            clauses.Add("o.acquired_at_utc_us >= $from");
            parameters.Add(new SqliteParameter("$from", from.Value));
        }

        var to = options.GetUtcMicroseconds("--to");
        if (to is not null)
        {
            clauses.Add("o.acquired_at_utc_us < $to");
            parameters.Add(new SqliteParameter("$to", to.Value));
        }

        var sql =
            "SELECT o.* FROM observations AS o " +
            "JOIN observation_rtree AS r ON r.observation_id = o.observation_id " +
            "WHERE " + string.Join(" AND ", clauses);
        return (sql, parameters.ToArray());
    }

    private static void AddSpatialFilter(
        ICollection<string> clauses,
        ICollection<object> parameters,
        string columnName,
        string parameterName,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        clauses.Add($"{columnName} = {parameterName}");
        parameters.Add(new SqliteParameter(parameterName, value));
    }

    private static (double West, double South, double East, double North) ParseBoundingBox(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var west) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var south) ||
            !double.TryParse(parts[2], CultureInfo.InvariantCulture, out var east) ||
            !double.TryParse(parts[3], CultureInfo.InvariantCulture, out var north))
        {
            throw new ArgumentException("--bbox must be west,south,east,north.");
        }

        if (west is < -180 or > 180 || east is < -180 or > 180 ||
            south is < -90 or > 90 || north is < -90 or > 90 || south > north)
        {
            throw new ArgumentException("Bounding box coordinates are out of range or inverted.");
        }

        return (west, south, east, north);
    }

    private static void WriteObservation(Observation observation)
    {
        var output = new ObservationOutput(
            observation.ObservationId,
            observation.Instrument,
            observation.Satellite,
            observation.AcquiredAtUtc,
            observation.Latitude,
            observation.Longitude,
            observation.Brightness,
            observation.BrightnessT31,
            observation.FireRadiativePower,
            observation.Scan,
            observation.Track,
            observation.DayNight,
            observation.VersionNumber,
            observation.VersionSpecifier,
            observation.ModisConfidencePercent,
            observation.ModisHotspotType,
            observation.ViirsConfidence,
            observation.ConfidenceClass);
        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            FireData.Cli — query and preprocess the SQLite fire-observation store.

            Commands:
              init     --database PATH
              query    --database PATH [--instrument MODIS|VIIRS|all]
                       [--from UTC] [--to UTC] [--confidence-class low|nominal|high]
                       [--bbox west,south,east,north] [--order-by time|frp|latitude|longitude]
                       [--limit N]                 (default 1000; 0 means no limit)
              bbox     --database PATH --bbox west,south,east,north [common filters]
              radius   --database PATH --lat DEG --lon DEG --radius-m METERS [common filters]
              stats    --database PATH [common filters]
              cluster  --database PATH --instrument MODIS|VIIRS
                       --epsilon-m METERS --min-points N [--from UTC] [--to UTC]
                       [--confidence-class low|nominal|high]

            Spatial radius uses a spherical Haversine distance. R*Tree is only the
            conservative candidate index; the returned radius/bbox rows are exact-
            filtered against stored coordinates. DBSCAN runs one instrument at a
            time because no cross-sensor sensitivity harmonization has been defined.
            Results and parameters are persisted in preprocessing_runs and
            cluster_memberships; noise is represented by a null cluster_id.
            """);
    }

    private readonly record struct SpatialPoint(long ObservationId, double Latitude, double Longitude);
}
