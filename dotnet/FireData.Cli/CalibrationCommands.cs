using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FireData.Cli;

internal static class CalibrationCommands
{
    private const string AlgorithmVersion = "sensor-calibration-csharp-v1";

    public static async Task<int> RunAsync(string databasePath, Arguments options)
    {
        var runId = options.GetInt("--run-id", -1);
        if (runId < 1)
        {
            throw new ArgumentException("--run-id must be a positive integer.");
        }

        var countStrategy = CalibrationStrategyRegistry.GetCount(
            options.Get("--count-method", "ratio"));
        var frpStrategy = CalibrationStrategyRegistry.Frp;

        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Foreign Keys=True");
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var metrics = await ReadDailyMetricsAsync(connection, transaction, runId);
        if (metrics.Count == 0)
        {
            throw new InvalidOperationException(
                $"Processing run {runId} has no daily-cell metrics; run process first.");
        }

        var pairs = PairMetrics(metrics);
        if (pairs.Count < 2)
        {
            throw new InvalidOperationException(
                "Calibration requires at least two same-day, same-cell MODIS/VIIRS metric pairs.");
        }

        var years = pairs.Select(x => x.Year).Distinct().Order().ToArray();
        if (years.Length < 2)
        {
            throw new InvalidOperationException(
                "Year-blocked calibration requires paired observations spanning at least two UTC years.");
        }

        var countFoldErrors = new List<double>();
        var countFoldSquaredErrors = new List<double>();
        var frpFoldErrors = new List<double>();
        var foldEvaluations = new List<object>();
        foreach (var heldOutYear in years)
        {
            var training = pairs.Where(x => x.Year != heldOutYear).ToArray();
            var holdout = pairs.Where(x => x.Year == heldOutYear).ToArray();
            if (training.Length < 2 || holdout.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Year-blocked calibration fold {heldOutYear} has insufficient paired training or holdout data.");
            }

            double[] countParameters;
            FrpMapping frpMapping;
            try
            {
                countParameters = countStrategy.Fit(training);
                frpMapping = frpStrategy.Fit(training);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException(
                    $"Year-blocked calibration fold {heldOutYear} is not valid: {error.Message}",
                    error);
            }

            var foldCountErrors = new List<double>(holdout.Length);
            var foldCountSquaredErrors = new List<double>(holdout.Length);
            var foldFrpErrors = new List<double>(holdout.Length);
            foreach (var pair in holdout)
            {
                var countError = countStrategy.Predict(pair.ModisCount, countParameters) - pair.ViirsCount;
                var absoluteCountError = Math.Abs(countError);
                countFoldErrors.Add(absoluteCountError);
                countFoldSquaredErrors.Add(countError * countError);
                foldCountErrors.Add(absoluteCountError);
                foldCountSquaredErrors.Add(countError * countError);
                var frpError = frpStrategy.Map(pair.ModisFrp, frpMapping) - pair.ViirsFrp;
                var absoluteFrpError = Math.Abs(frpError);
                frpFoldErrors.Add(absoluteFrpError);
                foldFrpErrors.Add(absoluteFrpError);
            }

            foldEvaluations.Add(new
            {
                heldOutYear,
                pairedDailyCells = holdout.Length,
                countMae = foldCountErrors.Average(),
                countRmse = Math.Sqrt(foldCountSquaredErrors.Average()),
                frpMae = foldFrpErrors.Average()
            });
        }

        var finalCountParameters = countStrategy.Fit(pairs);
        var finalFrpMapping = frpStrategy.Fit(pairs);
        var countErrorP90 = Quantile(countFoldErrors, 0.9);
        var frpErrorP90 = Quantile(frpFoldErrors, 0.9);
        var evaluation = new
        {
            validation = "leave-one-utc-year-out",
            pairedDailyCells = pairs.Count,
            heldOutYears = years,
            countMae = countFoldErrors.Average(),
            countRmse = Math.Sqrt(countFoldSquaredErrors.Average()),
            countAbsoluteErrorP90 = countErrorP90,
            frpMae = frpFoldErrors.Average(),
            frpAbsoluteErrorP90 = frpErrorP90,
            folds = foldEvaluations
        };
        var parameters = new
        {
            countMethod = countStrategy.Name,
            countParameters = finalCountParameters,
            countFeatures = countStrategy.Name == "poisson"
                ? new[] { "intercept", "log(1 + MODIS daily-cell detection count)" }
                : new[] { "VIIRS total / MODIS total" },
            frpMethod = frpStrategy.Name,
            frpPairedCells = finalFrpMapping.SourceQuantiles.Length,
            referenceSensor = "VIIRS",
            uncertainty = "90th percentile absolute error from year-blocked holdouts"
        };

        var createdAtUtcUs = SpatialMath.ToUnixMicroseconds(DateTimeOffset.UtcNow);
        var modelId = await InsertModelAsync(
            connection,
            transaction,
            runId,
            countStrategy.Name,
            JsonSerializer.Serialize(parameters),
            JsonSerializer.Serialize(evaluation),
            createdAtUtcUs);
        await InsertFrpMappingAsync(
            connection,
            transaction,
            modelId,
            finalFrpMapping);
        await InsertCalibratedMetricsAsync(
            connection,
            transaction,
            modelId,
            metrics,
            countStrategy,
            finalCountParameters,
            countErrorP90,
            frpStrategy,
            finalFrpMapping,
            frpErrorP90);
        await transaction.CommitAsync();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            calibrationModelId = modelId,
            preprocessingRunId = runId,
            countMethod = countStrategy.Name,
            frpMethod = frpStrategy.Name,
            referenceSensor = "VIIRS",
            evaluation
        }));
        return 0;
    }

    private static async Task<List<DailyMetric>> ReadDailyMetricsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT utc_date, grid_scheme, cell_x, cell_y, instrument,
                   detection_count, frp_sum
            FROM daily_cell_metrics
            WHERE preprocessing_run_id = $run_id
            ORDER BY utc_date, grid_scheme, cell_x, cell_y, instrument
            """);
        command.Parameters.AddWithValue("$run_id", runId);
        var rows = new List<DailyMetric>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DailyMetric(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetDouble(6)));
        }

        return rows;
    }

    private static List<CalibrationPair> PairMetrics(IReadOnlyCollection<DailyMetric> metrics)
    {
        var byCell = new Dictionary<DailyCellKey, (DailyMetric? Modis, DailyMetric? Viirs)>();
        foreach (var metric in metrics)
        {
            var key = new DailyCellKey(
                metric.UtcDate,
                metric.GridScheme,
                metric.CellX,
                metric.CellY);
            byCell.TryGetValue(key, out var sensors);
            sensors = metric.Instrument switch
            {
                "MODIS" => (metric, sensors.Viirs),
                "VIIRS" => (sensors.Modis, metric),
                _ => throw new InvalidOperationException(
                    $"Unexpected instrument in daily metrics: {metric.Instrument}")
            };
            byCell[key] = sensors;
        }

        var pairs = new List<CalibrationPair>();
        foreach (var (key, sensors) in byCell
                     .OrderBy(x => x.Key.UtcDate, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.GridScheme, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.CellX)
                     .ThenBy(x => x.Key.CellY))
        {
            if (sensors.Modis is null || sensors.Viirs is null)
            {
                continue;
            }

            if (!int.TryParse(
                    key.UtcDate.AsSpan(0, 4),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var year))
            {
                throw new InvalidOperationException($"Invalid UTC date in daily metrics: {key.UtcDate}");
            }

            pairs.Add(new CalibrationPair(
                year,
                sensors.Modis.Value.DetectionCount,
                sensors.Viirs.Value.DetectionCount,
                sensors.Modis.Value.FrpSum,
                sensors.Viirs.Value.FrpSum));
        }

        return pairs.OrderBy(x => x.Year).ToList();
    }

    private static async Task<long> InsertModelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        string countMethod,
        string parametersJson,
        string evaluationJson,
        long createdAtUtcUs)
    {
        await using (var delete = CreateCommand(
                         connection,
                         transaction,
                         """
                         DELETE FROM calibration_models
                         WHERE preprocessing_run_id = $run_id
                           AND count_method = $method
                           AND frp_method = 'empirical-quantile-mapping'
                         """))
        {
            delete.Parameters.AddWithValue("$run_id", runId);
            delete.Parameters.AddWithValue("$method", countMethod);
            await delete.ExecuteNonQueryAsync();
        }

        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO calibration_models (
                preprocessing_run_id, count_method, frp_method, reference_sensor,
                algorithm_version, parameters_json, evaluation_json, created_at_utc_us
            ) VALUES (
                $run_id, $count_method, 'empirical-quantile-mapping', 'VIIRS',
                $version, $parameters, $evaluation, $created
            );
            SELECT last_insert_rowid();
            """);
        insert.Parameters.AddWithValue("$run_id", runId);
        insert.Parameters.AddWithValue("$count_method", countMethod);
        insert.Parameters.AddWithValue("$version", AlgorithmVersion);
        insert.Parameters.AddWithValue("$parameters", parametersJson);
        insert.Parameters.AddWithValue("$evaluation", evaluationJson);
        insert.Parameters.AddWithValue("$created", createdAtUtcUs);
        return Convert.ToInt64(
            await insert.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertCalibratedMetricsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long modelId,
        IReadOnlyCollection<DailyMetric> metrics,
        ICountCalibrationStrategy countStrategy,
        IReadOnlyList<double> countParameters,
        double countErrorP90,
        IFrpCalibrationStrategy frpStrategy,
        FrpMapping frpMapping,
        double frpErrorP90)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO calibrated_daily_metrics (
                calibration_model_id, utc_date, grid_scheme, cell_x, cell_y, instrument,
                source_detection_count, calibrated_detection_count, count_lower, count_upper,
                source_frp_sum, calibrated_frp_sum, frp_lower, frp_upper
            ) VALUES (
                $model_id, $utc_date, $grid_scheme, $cell_x, $cell_y, $instrument,
                $source_count, $calibrated_count, $count_lower, $count_upper,
                $source_frp, $calibrated_frp, $frp_lower, $frp_upper
            )
            """);
        var modelIdParameter = command.Parameters.Add("$model_id", SqliteType.Integer);
        var dateParameter = command.Parameters.Add("$utc_date", SqliteType.Text);
        var gridParameter = command.Parameters.Add("$grid_scheme", SqliteType.Text);
        var xParameter = command.Parameters.Add("$cell_x", SqliteType.Integer);
        var yParameter = command.Parameters.Add("$cell_y", SqliteType.Integer);
        var instrumentParameter = command.Parameters.Add("$instrument", SqliteType.Text);
        var sourceCountParameter = command.Parameters.Add("$source_count", SqliteType.Integer);
        var calibratedCountParameter = command.Parameters.Add("$calibrated_count", SqliteType.Real);
        var countLowerParameter = command.Parameters.Add("$count_lower", SqliteType.Real);
        var countUpperParameter = command.Parameters.Add("$count_upper", SqliteType.Real);
        var sourceFrpParameter = command.Parameters.Add("$source_frp", SqliteType.Real);
        var calibratedFrpParameter = command.Parameters.Add("$calibrated_frp", SqliteType.Real);
        var frpLowerParameter = command.Parameters.Add("$frp_lower", SqliteType.Real);
        var frpUpperParameter = command.Parameters.Add("$frp_upper", SqliteType.Real);
        modelIdParameter.Value = modelId;

        foreach (var metric in metrics)
        {
            var isModis = metric.Instrument == "MODIS";
            var calibratedCount = isModis
                ? countStrategy.Predict(metric.DetectionCount, countParameters)
                : metric.DetectionCount;
            var calibratedFrp = isModis
                ? frpStrategy.Map(metric.FrpSum, frpMapping)
                : metric.FrpSum;
            dateParameter.Value = metric.UtcDate;
            gridParameter.Value = metric.GridScheme;
            xParameter.Value = metric.CellX;
            yParameter.Value = metric.CellY;
            instrumentParameter.Value = metric.Instrument;
            sourceCountParameter.Value = metric.DetectionCount;
            calibratedCountParameter.Value = calibratedCount;
            countLowerParameter.Value = isModis
                ? Math.Max(0, calibratedCount - countErrorP90)
                : calibratedCount;
            countUpperParameter.Value = isModis
                ? calibratedCount + countErrorP90
                : calibratedCount;
            sourceFrpParameter.Value = metric.FrpSum;
            calibratedFrpParameter.Value = calibratedFrp;
            frpLowerParameter.Value = isModis
                ? Math.Max(0, calibratedFrp - frpErrorP90)
                : calibratedFrp;
            frpUpperParameter.Value = isModis
                ? calibratedFrp + frpErrorP90
                : calibratedFrp;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertFrpMappingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long modelId,
        FrpMapping mapping)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO calibration_frp_quantiles (
                calibration_model_id, knot_index, modis_frp, viirs_frp
            ) VALUES ($model_id, $index, $modis_frp, $viirs_frp)
            """);
        var modelParameter = command.Parameters.Add("$model_id", SqliteType.Integer);
        var indexParameter = command.Parameters.Add("$index", SqliteType.Integer);
        var modisParameter = command.Parameters.Add("$modis_frp", SqliteType.Real);
        var viirsParameter = command.Parameters.Add("$viirs_frp", SqliteType.Real);
        modelParameter.Value = modelId;
        for (var index = 0; index < mapping.SourceQuantiles.Length; index++)
        {
            indexParameter.Value = index;
            modisParameter.Value = mapping.SourceQuantiles[index];
            viirsParameter.Value = mapping.ReferenceQuantiles[index];
            await command.ExecuteNonQueryAsync();
        }
    }

    private static double Quantile(IReadOnlyList<double> values, double probability)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException("Cannot compute an uncertainty quantile without holdout errors.");
        }

        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling(probability * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
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

    private readonly record struct DailyCellKey(
        string UtcDate,
        string GridScheme,
        long CellX,
        long CellY);

    private readonly record struct DailyMetric(
        string UtcDate,
        string GridScheme,
        long CellX,
        long CellY,
        string Instrument,
        long DetectionCount,
        double FrpSum);
}
