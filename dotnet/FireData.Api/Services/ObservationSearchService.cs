using FireData.Api.Models;
using FireData.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FireData.Api.Services;

/// <summary>Reads filtered observations from the shared SQLite database.</summary>
/// <param name="context">EF Core context for the fire-observation database.</param>
public sealed class ObservationSearchService(FireDataContext context) : IObservationSearchService
{
    private const long DayMicroseconds = 86_400_000_000;
    /// <inheritdoc />
    public async Task<ObservationSearchResponse> SearchAsync(
        ObservationSearchRequest request,
        CancellationToken cancellationToken)
    {
        var pageSize = request.PageSize ?? 100;
        var (query, radiusFilter) = BuildFilteredQuery(context, request);

        if (request is { AfterAcquiredAt: { } cursorTime, AfterObservationId: { } cursorId })
        {
            var cursorMicroseconds = SpatialMath.ToUnixMicroseconds(cursorTime);
            query = query.Where(observation =>
                observation.AcquiredAtUtcUs > cursorMicroseconds ||
                (observation.AcquiredAtUtcUs == cursorMicroseconds &&
                 observation.ObservationId > cursorId));
        }

        query = query
            .OrderBy(observation => observation.AcquiredAtUtcUs)
            .ThenBy(observation => observation.ObservationId);

        var matches = new List<ObservationResponse>(pageSize + 1);
        await foreach (var observation in query.AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            if (radiusFilter is { } radius &&
                SpatialMath.DistanceMeters(
                    radius.Latitude,
                    radius.Longitude,
                    observation.Latitude,
                    observation.Longitude) > radius.RadiusMeters)
            {
                continue;
            }

            matches.Add(ToResponse(observation));
            if (matches.Count > pageSize)
            {
                break;
            }
        }

        ObservationCursorResponse? next = null;
        if (matches.Count > pageSize)
        {
            matches.RemoveAt(matches.Count - 1);
            var last = matches[^1];
            next = new ObservationCursorResponse(last.AcquiredAt, last.ObservationId);
        }

        return new ObservationSearchResponse(matches, next);
    }

    internal static (
        IQueryable<Observation> Query,
        (double Latitude, double Longitude, double RadiusMeters)? RadiusFilter)
        BuildFilteredQuery(FireDataContext context, ObservationSearchRequest request)
    {
        var query = context.Observations.AsNoTracking();
        var radiusFilter = AddAreaFilter(context, ref query, request);
        query = ApplyPropertyFilters(query, request);
        query = ApplyDateAndTimeFilters(query, request);
        return (query, radiusFilter);
    }

    private static IQueryable<Observation> ApplyPropertyFilters(
        IQueryable<Observation> query,
        ObservationSearchRequest request)
    {
        if (request.Confidence is { } confidence)
        {
            var normalized = NormalizeConfidence(confidence);
            query = query.Where(observation => observation.ConfidenceClass == normalized);
        }

        if (request.Satellite is { } satellite)
        {
            var normalized = satellite.ToUpperInvariant();
            query = query.Where(observation => observation.Satellite == normalized);
        }

        if (request.BrightnessMin is { } brightnessMin)
        {
            query = query.Where(observation => observation.Brightness >= brightnessMin);
        }

        if (request.BrightnessMax is { } brightnessMax)
        {
            query = query.Where(observation => observation.Brightness <= brightnessMax);
        }

        if (request.FrpMin is { } frpMin)
        {
            query = query.Where(observation => observation.FireRadiativePower >= frpMin);
        }

        if (request.FrpMax is { } frpMax)
        {
            query = query.Where(observation => observation.FireRadiativePower <= frpMax);
        }

        if (request.Type is { } type)
        {
            var normalized = type.ToUpperInvariant();
            query = query.Where(observation => observation.ModisHotspotType == normalized);
        }

        return query;
    }

    private static IQueryable<Observation> ApplyDateAndTimeFilters(
        IQueryable<Observation> query,
        ObservationSearchRequest request)
    {
        if (request.DateFrom is { } dateFrom)
        {
            var lowerBound = ToUtcMicroseconds(dateFrom);
            query = query.Where(observation => observation.AcquiredAtUtcUs >= lowerBound);
        }

        if (request.DateTo is { } dateTo)
        {
            var upperBound = dateTo == DateOnly.MaxValue
                ? long.MaxValue
                : ToUtcMicroseconds(dateTo.AddDays(1));
            query = query.Where(observation => observation.AcquiredAtUtcUs < upperBound);
        }

        if (request.AcquiredFrom is { } acquiredFrom)
        {
            var lowerBound = SpatialMath.ToUnixMicroseconds(acquiredFrom);
            query = query.Where(observation => observation.AcquiredAtUtcUs >= lowerBound);
        }

        if (request.AcquiredTo is { } acquiredTo)
        {
            var upperBound = SpatialMath.ToUnixMicroseconds(acquiredTo);
            query = query.Where(observation => observation.AcquiredAtUtcUs < upperBound);
        }

        if (request.TimeOfDayFrom is not null ||
            request.TimeOfDayTo is not null)
        {
            var fromMicroseconds = request.TimeOfDayFrom is { } from
                ? from.Ticks / 10
                : 0;
            var toMicroseconds = request.TimeOfDayTo is { } to
                ? to.Ticks / 10
                : DayMicroseconds;

            query = request.TimeOfDayFrom is not null &&
                    request.TimeOfDayTo is not null &&
                    fromMicroseconds > toMicroseconds
                ? query.Where(observation =>
                    ((observation.AcquiredAtUtcUs % DayMicroseconds + DayMicroseconds) %
                     DayMicroseconds) >= fromMicroseconds ||
                    ((observation.AcquiredAtUtcUs % DayMicroseconds + DayMicroseconds) %
                     DayMicroseconds) < toMicroseconds)
                : query.Where(observation =>
                    ((observation.AcquiredAtUtcUs % DayMicroseconds + DayMicroseconds) %
                     DayMicroseconds) >= fromMicroseconds &&
                    ((observation.AcquiredAtUtcUs % DayMicroseconds + DayMicroseconds) %
                     DayMicroseconds) < toMicroseconds);
        }

        return query;
    }

    private static (double Latitude, double Longitude, double RadiusMeters)? AddAreaFilter(
        FireDataContext context,
        ref IQueryable<Observation> query,
        ObservationSearchRequest request)
    {
        if (request.West is { } west &&
            request.South is { } south &&
            request.East is { } east &&
            request.North is { } north)
        {
            var intervals = west <= east
                ? new[] { new LongitudeInterval(west, east) }
                : new[]
                {
                    new LongitudeInterval(west, 180),
                    new LongitudeInterval(-180, east)
                };
            query = AddRTreeCandidates(
                context,
                south,
                north,
                intervals);
            query = query.Where(observation =>
                observation.Latitude >= south &&
                observation.Latitude <= north &&
                (west <= east
                    ? observation.Longitude >= west && observation.Longitude <= east
                    : observation.Longitude >= west || observation.Longitude <= east));
            return null;
        }

        if (request.CenterLatitude is { } latitude &&
            request.CenterLongitude is { } longitude &&
            request.RadiusMeters is { } radius)
        {
            var envelope = SpatialMath.EnvelopeForRadius(latitude, longitude, radius);
            query = AddRTreeCandidates(
                context,
                envelope.South,
                envelope.North,
                envelope.LongitudeIntervals);
            return (latitude, longitude, radius);
        }

        return null;
    }

    private static IQueryable<Observation> AddRTreeCandidates(
        FireDataContext context,
        double south,
        double north,
        IReadOnlyList<LongitudeInterval> intervals)
    {
        var longitudePredicates = new List<string>(intervals.Count);
        var parameters = new List<object>
        {
            new SqliteParameter("$south", south),
            new SqliteParameter("$north", north)
        };

        for (var index = 0; index < intervals.Count; index++)
        {
            var interval = intervals[index];
            var westName = $"$west{index}";
            var eastName = $"$east{index}";
            longitudePredicates.Add(
                $"(r.min_longitude <= {eastName} AND r.max_longitude >= {westName})");
            parameters.Add(new SqliteParameter(westName, interval.West));
            parameters.Add(new SqliteParameter(eastName, interval.East));
        }

        var sql =
            """
            SELECT o.*
            FROM observations AS o
            JOIN observation_rtree AS r ON r.observation_id = o.observation_id
            WHERE r.min_latitude <= $north
              AND r.max_latitude >= $south
              AND (
            """ +
            string.Join(" OR ", longitudePredicates) +
            ")";
        return context.Observations
            .FromSqlRaw(sql, parameters.ToArray())
            .AsNoTracking();
    }

    private static long ToUtcMicroseconds(DateOnly date) =>
        SpatialMath.ToUnixMicroseconds(
            new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private static string NormalizeConfidence(string confidence) =>
        confidence.ToUpperInvariant() switch
        {
            "LOW" => "Low",
            "NOMINAL" => "Nominal",
            "HIGH" => "High",
            _ => throw new InvalidOperationException(
                $"Unexpected confidence class: {confidence}")
        };

    private static ObservationResponse ToResponse(Observation observation) =>
        new(
            observation.ObservationId,
            observation.Instrument,
            observation.Satellite,
            observation.AcquiredAtUtc,
            observation.Latitude,
            observation.Longitude,
            observation.Brightness,
            observation.FireRadiativePower,
            observation.Scan,
            observation.Track,
            observation.ConfidenceClass,
            observation.ModisHotspotType);
}
