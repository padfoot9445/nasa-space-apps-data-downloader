using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FireData.Cli.Tests;

public sealed class ProcessingPipelineTests
{
    [Fact]
    public async Task ProcessAndCalibratePreservesRawRowsAndPersistsDerivedProducts()
    {
        var databasePath = NewDatabasePath();
        try
        {
            await SeedProcessingDatabaseAsync(databasePath);

            var processExitCode = await Commands.RunAsync(
            [
                "process",
                "--database", databasePath,
                "--grid-degrees", "7",
                "--epsilon-m", "2000",
                "--time-scale-m-per-day", "1500",
                "--min-points", "2"
            ]);

            Assert.Equal(0, processExitCode);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            Assert.Equal(20L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM observations"));
            Assert.Equal(19L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1"));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id IS NULL"));
            Assert.Equal(6L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM processing_events WHERE preprocessing_run_id = 1"));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                """
                SELECT COUNT(*) FROM processing_run_sources
                WHERE preprocessing_run_id = 1
                  AND selected_observation_count = 20
                  AND processed_observation_count = 19
                  AND source_sha256 = 'fixture'
                """));
            Assert.Equal(7L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM daily_cell_metrics WHERE preprocessing_run_id = 1"));
            Assert.Equal(0L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM daily_cell_metrics WHERE detection_count <= 0"));
            Assert.True(await ScalarDoubleAsync(
                connection,
                """
                SELECT MAX(cell_area_km2) > MIN(cell_area_km2)
                FROM daily_cell_metrics
                WHERE preprocessing_run_id = 1 AND cell_y = 18
                """));
            Assert.True(await ScalarLongAsync(
                connection,
                """
                SELECT COUNT(*) FROM daily_cell_presence_heuristic
                WHERE preprocessing_run_id = 1 AND has_direct_detection = 0
                """) > 0);

            Assert.Equal(0, await Commands.RunAsync(
            [
                "calibrate",
                "--database", databasePath,
                "--run-id", "1",
                "--count-method", "ratio"
            ]));
            Assert.Equal(0, await Commands.RunAsync(
            [
                "calibrate",
                "--database", databasePath,
                "--run-id", "1",
                "--count-method", "poisson"
            ]));
            Assert.Equal(2L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM calibration_models WHERE preprocessing_run_id = 1"));
            Assert.Equal(14L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM calibrated_daily_metrics"));
            Assert.Equal(6L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM calibration_frp_quantiles"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MigrateAddsProcessingTablesWithoutChangingExistingRows()
    {
        var databasePath = NewDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE preprocessing_runs (
                        preprocessing_run_id INTEGER PRIMARY KEY,
                        run_kind TEXT NOT NULL,
                        algorithm_version TEXT NOT NULL,
                        created_at_utc_us INTEGER NOT NULL,
                        parameters_json TEXT NOT NULL,
                        output_schema_json TEXT NOT NULL DEFAULT '{}'
                    );
                    CREATE TABLE observations (observation_id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
                    INSERT INTO observations VALUES (7, 'keep');
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(0, await Commands.RunAsync(["migrate", "--database", databasePath]));

            await using var migrated = new SqliteConnection($"Data Source={databasePath}");
            await migrated.OpenAsync();
            Assert.Equal(2L, await ScalarLongAsync(migrated, "PRAGMA user_version"));
            Assert.Equal(1L, await ScalarLongAsync(
                migrated,
                "SELECT COUNT(*) FROM observations WHERE observation_id = 7 AND payload = 'keep'"));
            Assert.Equal(1L, await ScalarLongAsync(
                migrated,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'calibrated_daily_metrics'"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ProcessesNeighborhoodsLargerThanOneQueueBatch()
    {
        var databasePath = NewDatabasePath();
        try
        {
            await CreateDatabaseWithSourceAsync(databasePath, 1_040);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                long recordNumber = 0;
                var acquiredAt = SpatialMath.ToUnixMicroseconds(
                    new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
                foreach (var instrument in new[] { "MODIS", "VIIRS" })
                {
                    for (var index = 0; index < 520; index++)
                    {
                        recordNumber++;
                        await InsertObservationAsync(
                            connection,
                            recordNumber,
                            instrument,
                            acquiredAt + index * 1_000_000,
                            37,
                            -119.9,
                            1,
                            scan: 1);
                    }
                }
            }

            Assert.Equal(0, await Commands.RunAsync(
            [
                "process",
                "--database", databasePath,
                "--epsilon-m", "2000",
                "--time-scale-m-per-day", "1500",
                "--min-points", "2"
            ]));

            await using var results = new SqliteConnection($"Data Source={databasePath}");
            await results.OpenAsync();
            Assert.Equal(1_040L, await ScalarLongAsync(
                results,
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id IS NOT NULL"));
            Assert.Equal(2L, await ScalarLongAsync(
                results,
                "SELECT COUNT(*) FROM processing_events WHERE preprocessing_run_id = 1 AND observation_count = 520"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task AssignsPreviouslyNoisyPointAsBorderAndRetainsIsolatedNoise()
    {
        var databasePath = NewDatabasePath();
        try
        {
            await CreateDatabaseWithSourceAsync(databasePath, 5);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                var acquiredAt = SpatialMath.ToUnixMicroseconds(
                    new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
                var points = new[]
                {
                    (Latitude: 37.0, Longitude: 0.0012),
                    (Latitude: 37.0, Longitude: -0.0004),
                    (Latitude: 37.0, Longitude: 0.0),
                    (Latitude: 37.0, Longitude: 0.0004),
                    (Latitude: 38.0, Longitude: 1.0)
                };
                for (var index = 0; index < points.Length; index++)
                {
                    await InsertObservationAsync(
                        connection,
                        index + 1,
                        "MODIS",
                        acquiredAt,
                        points[index].Latitude,
                        points[index].Longitude,
                        1,
                        scan: 1);
                }
            }

            Assert.Equal(0, await Commands.RunAsync(
            [
                "process",
                "--database", databasePath,
                "--epsilon-m", "100",
                "--time-scale-m-per-day", "1500",
                "--min-points", "3"
            ]));

            await using var results = new SqliteConnection($"Data Source={databasePath}");
            await results.OpenAsync();
            Assert.Equal(4L, await ScalarLongAsync(
                results,
                """
                SELECT COUNT(*) FROM cluster_memberships
                WHERE preprocessing_run_id = 1 AND cluster_id > 0
                """));
            Assert.Equal(1L, await ScalarLongAsync(
                results,
                """
                SELECT COUNT(*) FROM cluster_memberships
                WHERE preprocessing_run_id = 1 AND cluster_id IS NULL
                """));
            Assert.Equal(1L, await ScalarLongAsync(
                results,
                """
                SELECT COUNT(*) FROM cluster_memberships
                WHERE preprocessing_run_id = 1 AND is_core_point = 0 AND cluster_id > 0
                """));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task CreateDatabaseWithSourceAsync(string databasePath, long recordCount)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            await schema.ExecuteNonQueryAsync();
        }

        await using var source = connection.CreateCommand();
        source.CommandText =
            """
            INSERT INTO source_files (
                source_path, source_name, product, sha256, imported_at_utc_us,
                importer_version, schema_version, record_count, accepted_count, error_count
            ) VALUES ('fixture', 'fixture.json', 'test', 'fixture', 0, 'test', 2, $count, $count, 0)
            """;
        source.Parameters.AddWithValue("$count", recordCount);
        await source.ExecuteNonQueryAsync();
    }

    private static async Task SeedProcessingDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            await schema.ExecuteNonQueryAsync();
        }

        await using (var source = connection.CreateCommand())
        {
            source.CommandText =
                """
                INSERT INTO source_files (
                    source_path, source_name, product, sha256, imported_at_utc_us,
                    importer_version, schema_version, record_count, accepted_count, error_count
                ) VALUES ('fixture', 'fixture.json', 'test', 'fixture', 0, 'test', 2, 20, 20, 0)
                """;
            await source.ExecuteNonQueryAsync();
        }

        long recordNumber = 0;
        foreach (var (year, pointCount) in new[] { (2020, 2), (2021, 3), (2022, 4) })
        {
            var acquiredAt = SpatialMath.ToUnixMicroseconds(
                new DateTimeOffset(year, 6, 1, 12, 0, 0, TimeSpan.Zero));
            foreach (var instrument in new[] { "MODIS", "VIIRS" })
            {
                for (var index = 0; index < pointCount; index++)
                {
                    recordNumber++;
                    await InsertObservationAsync(
                        connection,
                        recordNumber,
                        instrument,
                        acquiredAt + index * 1_000_000,
                        37.0 + index * 0.00001,
                        -119.9 + index * 0.00001,
                        10 + year - 2020 + index + (instrument == "VIIRS" ? 10 : 0),
                        scan: 1);
                }
            }
        }

        recordNumber++;
        await InsertObservationAsync(
            connection,
            recordNumber,
            "MODIS",
            SpatialMath.ToUnixMicroseconds(
                new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero)),
            37,
            179.999,
            12,
            scan: 1);
        recordNumber++;
        await InsertObservationAsync(
            connection,
            recordNumber,
            "VIIRS",
            SpatialMath.ToUnixMicroseconds(
                new DateTimeOffset(2022, 6, 1, 12, 0, 0, TimeSpan.Zero)),
            37,
            -119.9,
            12,
            scan: 0);
    }

    private static async Task InsertObservationAsync(
        SqliteConnection connection,
        long recordNumber,
        string instrument,
        long acquiredAtUtcUs,
        double latitude,
        double longitude,
        double frp,
        double scan)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO observations (
                source_file_id, source_record_number, instrument, satellite, acquired_at_utc_us,
                latitude, longitude, brightness, scan, track, version_number, version_specifier,
                brightness_t31, fire_radiative_power, day_night,
                modis_confidence_percent, viirs_confidence
            ) VALUES (
                1, $record, $instrument, $satellite, $acquired, $latitude, $longitude,
                320, $scan, 1, 1, 'NRT', 300, $frp, 'D', $modis_confidence, $viirs_confidence
            )
            """;
        command.Parameters.AddWithValue("$record", recordNumber);
        command.Parameters.AddWithValue("$instrument", instrument);
        command.Parameters.AddWithValue("$satellite", instrument == "MODIS" ? "AQUA" : "N");
        command.Parameters.AddWithValue("$acquired", acquiredAtUtcUs);
        command.Parameters.AddWithValue("$latitude", latitude);
        command.Parameters.AddWithValue("$longitude", longitude);
        command.Parameters.AddWithValue("$scan", scan);
        command.Parameters.AddWithValue("$frp", frp);
        command.Parameters.AddWithValue(
            "$modis_confidence",
            instrument == "MODIS" ? 80 : DBNull.Value);
        command.Parameters.AddWithValue(
            "$viirs_confidence",
            instrument == "VIIRS" ? "HIGH" : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ScalarDoubleAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0;
    }

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fire-data-test-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var file in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
