using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FireData.Cli;

internal static class DatabaseDbscan
{
    private const long MicrosecondsPerDay = 86_400_000_000;
    private const int BatchSize = 256;
    private const int CandidateBatchSize = 512;
    private const int NeighborInsertBatchSize = 500;
    private const int ParallelCandidateThreshold = 256;

    public static async Task<int> ClusterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrument,
        double epsilonMeters,
        double timeScaleMetersPerDay,
        int minPoints)
    {
        var clusterCount = 0;
        var scanned = 0L;
        long lastObservationId = 0;
        var stopwatch = Stopwatch.StartNew();
        Console.Error.WriteLine($"Clustering {instrument} observations...");

        while (true)
        {
            var seeds = await ReadSeedBatchAsync(
                connection,
                transaction,
                instrument,
                lastObservationId);
            if (seeds.Count == 0)
            {
                break;
            }

            lastObservationId = seeds[^1].ObservationId;
            foreach (var seed in seeds)
            {
                scanned++;
                if (!await TryClaimSeedAsync(connection, transaction, seed.ObservationId))
                {
                    continue;
                }

                await ExecuteAsync(connection, transaction, "DELETE FROM temp.processing_queue");
                var clusterId = clusterCount + 1;
                var isCore = await ExpandPointAsync(
                    connection,
                    transaction,
                    instrument,
                    seed,
                    clusterId,
                    epsilonMeters,
                    timeScaleMetersPerDay,
                    minPoints);
                if (!isCore)
                {
                    continue;
                }

                clusterCount = clusterId;
                while (await ExpandQueueBatchAsync(
                           connection,
                           transaction,
                           instrument,
                           clusterId,
                           epsilonMeters,
                           timeScaleMetersPerDay,
                           minPoints))
                {
                }
            }

            if (scanned % 25_000 < seeds.Count)
            {
                Console.Error.WriteLine(
                    $"  {instrument}: scanned {scanned:N0} candidates, " +
                    $"{clusterCount:N0} clusters, {stopwatch.Elapsed:g} elapsed");
            }
        }

        Console.Error.WriteLine(
            $"  {instrument}: complete; {scanned:N0} candidates, " +
            $"{clusterCount:N0} clusters, {stopwatch.Elapsed:g} elapsed");
        return clusterCount;
    }

    private static async Task<List<SpatialPoint>> ReadSeedBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrument,
        long afterId)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT observation_id, acquired_at_utc_us, latitude, longitude
            FROM temp.processing_points
            WHERE instrument = $instrument AND observation_id > $after_id
            ORDER BY observation_id LIMIT $batch_size
            """);
        command.Parameters.AddWithValue("$instrument", instrument);
        command.Parameters.AddWithValue("$after_id", afterId);
        command.Parameters.AddWithValue("$batch_size", BatchSize);
        var points = new List<SpatialPoint>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            points.Add(new SpatialPoint(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetDouble(2),
                reader.GetDouble(3)));
        }

        return points;
    }

    private static async Task<bool> TryClaimSeedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long observationId)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE temp.processing_points
            SET visited = 1, cluster_id = -1
            WHERE observation_id = $observation_id AND visited = 0
            """);
        command.Parameters.AddWithValue("$observation_id", observationId);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    private static async Task<bool> ExpandPointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrument,
        SpatialPoint point,
        int clusterId,
        double epsilonMeters,
        double timeScaleMetersPerDay,
        int minPoints)
    {
        var envelope = SpatialMath.EnvelopeForRadius(
            point.Latitude,
            point.Longitude,
            epsilonMeters);
        var intervals = envelope.LongitudeIntervals;
        var windowMicroseconds = (long)Math.Ceiling(
            epsilonMeters / timeScaleMetersPerDay * MicrosecondsPerDay);
        var minTime = point.AcquiredAtUtcUs < long.MinValue + windowMicroseconds
            ? long.MinValue
            : point.AcquiredAtUtcUs - windowMicroseconds;
        var maxTime = point.AcquiredAtUtcUs > long.MaxValue - windowMicroseconds
            ? long.MaxValue
            : point.AcquiredAtUtcUs + windowMicroseconds;
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM temp.processing_neighbors");
        var candidates = new List<SpatialPoint>(CandidateBatchSize);
        var neighborIds = new List<long>();
        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         """
                         SELECT p.observation_id, p.acquired_at_utc_us, p.latitude, p.longitude
                         FROM observation_rtree AS r
                         JOIN temp.processing_points AS p ON p.observation_id = r.observation_id
                         WHERE p.instrument = $instrument
                           AND p.acquired_at_utc_us BETWEEN $min_time AND $max_time
                           AND r.min_latitude <= $north AND r.max_latitude >= $south
                           AND ((r.min_longitude <= $east0 AND r.max_longitude >= $west0)
                                OR ($has_second = 1
                                    AND r.min_longitude <= $east1 AND r.max_longitude >= $west1))
                         """))
        {
            command.Parameters.AddWithValue("$instrument", instrument);
            command.Parameters.AddWithValue("$min_time", minTime);
            command.Parameters.AddWithValue("$max_time", maxTime);
            command.Parameters.AddWithValue("$south", envelope.South);
            command.Parameters.AddWithValue("$north", envelope.North);
            command.Parameters.AddWithValue("$west0", intervals[0].West);
            command.Parameters.AddWithValue("$east0", intervals[0].East);
            command.Parameters.AddWithValue("$has_second", intervals.Count > 1 ? 1 : 0);
            command.Parameters.AddWithValue("$west1", intervals.Count > 1 ? intervals[1].West : 0);
            command.Parameters.AddWithValue("$east1", intervals.Count > 1 ? intervals[1].East : 0);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add(new SpatialPoint(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3)));
                if (candidates.Count == CandidateBatchSize)
                {
                    FindNeighbors(point, candidates, epsilonMeters, timeScaleMetersPerDay, neighborIds);
                    candidates.Clear();
                }
            }
        }

        if (candidates.Count > 0)
        {
            FindNeighbors(point, candidates, epsilonMeters, timeScaleMetersPerDay, neighborIds);
        }

        await InsertNeighborBatchAsync(connection, transaction, neighborIds);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO temp.processing_core_points (observation_id)
            SELECT $center_id
            WHERE (SELECT COUNT(*) FROM temp.processing_neighbors) >= $min_points;

            INSERT OR IGNORE INTO temp.processing_queue (observation_id)
            SELECT observation_id FROM temp.processing_neighbors
            WHERE observation_id <> $center_id
              AND EXISTS (
                  SELECT 1 FROM temp.processing_core_points
                  WHERE observation_id = $center_id
              );

            UPDATE temp.processing_points
            SET cluster_id = $cluster_id, is_core_point = 1
            WHERE observation_id = $center_id
              AND EXISTS (
                  SELECT 1 FROM temp.processing_core_points
                  WHERE observation_id = $center_id
              );
            """,
            new SqliteParameter("$cluster_id", clusterId),
            new SqliteParameter("$center_id", point.ObservationId),
            new SqliteParameter("$min_points", minPoints));
        return await ScalarLongAsync(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1 FROM temp.processing_core_points
                WHERE observation_id = $center_id
            )
            """,
            new SqliteParameter("$center_id", point.ObservationId)) != 0;
    }

    private static void FindNeighbors(
        SpatialPoint center,
        IReadOnlyList<SpatialPoint> candidates,
        double epsilonMeters,
        double timeScaleMetersPerDay,
        List<long> neighborIds)
    {
        if (candidates.Count >= ParallelCandidateThreshold)
        {
            var isNeighbor = new bool[candidates.Count];
            Parallel.For(
                0,
                candidates.Count,
                index => isNeighbor[index] = IsNeighbor(
                    center,
                    candidates[index],
                    epsilonMeters,
                    timeScaleMetersPerDay));
            for (var index = 0; index < candidates.Count; index++)
            {
                if (isNeighbor[index])
                {
                    neighborIds.Add(candidates[index].ObservationId);
                }
            }
        }
        else
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (IsNeighbor(center, candidates[index], epsilonMeters, timeScaleMetersPerDay))
                {
                    neighborIds.Add(candidates[index].ObservationId);
                }
            }
        }
    }

    private static bool IsNeighbor(
        SpatialPoint center,
        SpatialPoint candidate,
        double epsilonMeters,
        double timeScaleMetersPerDay)
    {
        var distance = SpatialMath.DistanceMeters(
            center.Latitude,
            center.Longitude,
            candidate.Latitude,
            candidate.Longitude);
        var elapsedDays = Math.Abs(
            (candidate.AcquiredAtUtcUs - (double)center.AcquiredAtUtcUs) / MicrosecondsPerDay);
        var temporalDistance = elapsedDays * timeScaleMetersPerDay;
        return distance * distance + temporalDistance * temporalDistance <= epsilonMeters * epsilonMeters;
    }

    private static async Task InsertNeighborBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> observationIds)
    {
        for (var offset = 0; offset < observationIds.Count; offset += NeighborInsertBatchSize)
        {
            var count = Math.Min(NeighborInsertBatchSize, observationIds.Count - offset);
            var sql = new StringBuilder("INSERT OR IGNORE INTO temp.processing_neighbors (observation_id) VALUES ");
            await using var command = CreateCommand(connection, transaction, string.Empty);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"$id{index}";
                sql.Append('(').Append(parameterName).Append(')');
                command.Parameters.AddWithValue(parameterName, observationIds[offset + index]);
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> ExpandQueueBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instrument,
        int clusterId,
        double epsilonMeters,
        double timeScaleMetersPerDay,
        int minPoints)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM temp.processing_frontier;
            INSERT INTO temp.processing_frontier (
                queue_id, observation_id, acquired_at_utc_us, latitude, longitude,
                was_visited, previous_cluster_id
            )
            SELECT q.queue_id, p.observation_id, p.acquired_at_utc_us, p.latitude,
                   p.longitude, p.visited, p.cluster_id
            FROM temp.processing_queue AS q
            JOIN temp.processing_points AS p ON p.observation_id = q.observation_id
            WHERE p.instrument = $instrument AND q.processed = 0
            ORDER BY q.queue_id
            LIMIT $batch_size;
            """,
            new SqliteParameter("$instrument", instrument),
            new SqliteParameter("$batch_size", BatchSize));
        var frontierCount = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM temp.processing_frontier");
        if (frontierCount == 0)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM temp.processing_queue");
            return false;
        }

        var clusterParameter = new SqliteParameter("$cluster_id", clusterId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE temp.processing_queue
            SET processed = 1
            WHERE queue_id IN (SELECT queue_id FROM temp.processing_frontier);
            UPDATE temp.processing_points
            SET cluster_id = $cluster_id
            WHERE observation_id IN (
                SELECT observation_id FROM temp.processing_frontier
            ) AND cluster_id <= 0;
            UPDATE temp.processing_points
            SET visited = 1
            WHERE observation_id IN (
                SELECT observation_id FROM temp.processing_frontier
            ) AND visited = 0;
            """,
            clusterParameter);

        var frontier = new List<SpatialPoint>((int)frontierCount);
        var previouslyVisited = new HashSet<long>();
        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         """
                         SELECT observation_id, acquired_at_utc_us, latitude, longitude, was_visited
                         FROM temp.processing_frontier ORDER BY queue_id
                         """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var point = new SpatialPoint(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3));
                frontier.Add(point);
                if (reader.GetInt32(4) != 0)
                {
                    previouslyVisited.Add(point.ObservationId);
                }
            }
        }

        foreach (var point in frontier)
        {
            if (previouslyVisited.Contains(point.ObservationId))
            {
                continue;
            }

            await ExpandPointAsync(
                connection,
                transaction,
                instrument,
                point,
                clusterId,
                epsilonMeters,
                timeScaleMetersPerDay,
                minPoints);
        }

        return true;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params SqliteParameter[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddRange(parameters);
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

    private readonly record struct SpatialPoint(
        long ObservationId,
        long AcquiredAtUtcUs,
        double Latitude,
        double Longitude);
}
