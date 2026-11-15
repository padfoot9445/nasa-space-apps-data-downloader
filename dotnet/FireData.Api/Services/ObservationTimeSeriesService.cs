using FireData.Api.Models;
using FireData.Cli;
using Microsoft.EntityFrameworkCore;

namespace FireData.Api.Services;

/// <summary>Aggregates daily sensor counts in SQLite using the shared observation filters.</summary>
public sealed class ObservationTimeSeriesService(FireDataContext context)
    : IObservationTimeSeriesService
{
    private const long DayMicroseconds = 86_400_000_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObservationTimeSeriesPoint>> GetAsync(
        ObservationSearchRequest request,
        CancellationToken cancellationToken)
    {
        var (query, radiusFilter) = ObservationSearchService.BuildFilteredQuery(context, request);
        var counts = new Dictionary<(long DayIndex, string Instrument), long>();

        if (radiusFilter is null)
        {
            var groupedCounts = await query
                .GroupBy(observation => new
                {
                    observation.Instrument,
                    DayIndex =
                        (observation.AcquiredAtUtcUs -
                         (observation.AcquiredAtUtcUs < 0 ? DayMicroseconds - 1 : 0)) /
                        DayMicroseconds
                })
                .Select(group => new
                {
                    group.Key.DayIndex,
                    group.Key.Instrument,
                    Count = group.LongCount()
                })
                .ToListAsync(cancellationToken);

            foreach (var group in groupedCounts)
            {
                counts[(group.DayIndex, group.Instrument)] = group.Count;
            }
        }
        else
        {
            var radius = radiusFilter.Value;
            await foreach (var observation in query
                               .Select(observation => new
                               {
                                   observation.Instrument,
                                   observation.AcquiredAtUtcUs,
                                   observation.Latitude,
                                   observation.Longitude
                               })
                               .AsAsyncEnumerable()
                               .WithCancellation(cancellationToken))
            {
                if (SpatialMath.DistanceMeters(
                        radius.Latitude,
                        radius.Longitude,
                        observation.Latitude,
                        observation.Longitude) > radius.RadiusMeters)
                {
                    continue;
                }

                var dayIndex = FloorDayIndex(observation.AcquiredAtUtcUs);
                var key = (dayIndex, observation.Instrument);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        if (counts.Count == 0)
        {
            return [];
        }

        var firstDay = counts.Keys.Min(key => key.DayIndex);
        var lastDay = counts.Keys.Max(key => key.DayIndex);
        var points = new List<ObservationTimeSeriesPoint>((int)(lastDay - firstDay + 1));
        for (var dayIndex = firstDay; dayIndex <= lastDay; dayIndex++)
        {
            var date = DateOnly.FromDateTime(
                DateTime.UnixEpoch.AddTicks(dayIndex * TimeSpan.TicksPerDay));
            points.Add(new ObservationTimeSeriesPoint(
                date,
                counts.GetValueOrDefault((dayIndex, "MODIS")),
                counts.GetValueOrDefault((dayIndex, "VIIRS"))));
        }

        return points;
    }

    private static long FloorDayIndex(long timestamp) =>
        timestamp >= 0
            ? timestamp / DayMicroseconds
            : (timestamp - DayMicroseconds + 1) / DayMicroseconds;
}
