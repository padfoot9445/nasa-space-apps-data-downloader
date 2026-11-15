using Microsoft.AspNetCore.Mvc;

namespace FireData.Api.Models;

/// <summary>Query filters and continuation cursor for active-fire observations.</summary>
public sealed record ObservationSearchRequest
{
    /// <summary>First acquisition date to include, interpreted in UTC.</summary>
    [FromQuery(Name = "dateFrom")]
    public DateOnly? DateFrom { get; init; }

    /// <summary>Last acquisition date to include, interpreted in UTC.</summary>
    [FromQuery(Name = "dateTo")]
    public DateOnly? DateTo { get; init; }

    /// <summary>Inclusive acquisition timestamp lower bound; ISO 8601 with an explicit UTC offset.</summary>
    [FromQuery(Name = "acquiredFrom")]
    public DateTimeOffset? AcquiredFrom { get; init; }

    /// <summary>Exclusive acquisition timestamp upper bound; ISO 8601 with an explicit UTC offset.</summary>
    [FromQuery(Name = "acquiredTo")]
    public DateTimeOffset? AcquiredTo { get; init; }

    /// <summary>Inclusive UTC time-of-day lower bound. May be later than TimeOfDayTo to cross midnight.</summary>
    [FromQuery(Name = "timeOfDayFrom")]
    public TimeOnly? TimeOfDayFrom { get; init; }

    /// <summary>Exclusive UTC time-of-day upper bound.</summary>
    [FromQuery(Name = "timeOfDayTo")]
    public TimeOnly? TimeOfDayTo { get; init; }

    /// <summary>Western longitude of a bounding-box area of interest; use a greater east longitude to cross the antimeridian.</summary>
    [FromQuery(Name = "west")]
    public double? West { get; init; }

    /// <summary>Southern latitude of a bounding-box area of interest.</summary>
    [FromQuery(Name = "south")]
    public double? South { get; init; }

    /// <summary>Eastern longitude of a bounding-box area of interest.</summary>
    [FromQuery(Name = "east")]
    public double? East { get; init; }

    /// <summary>Northern latitude of a bounding-box area of interest.</summary>
    [FromQuery(Name = "north")]
    public double? North { get; init; }

    /// <summary>Latitude of a circular area of interest's center; use with CenterLongitude and RadiusMeters.</summary>
    [FromQuery(Name = "centerLatitude")]
    public double? CenterLatitude { get; init; }

    /// <summary>Longitude of a circular area of interest's center.</summary>
    [FromQuery(Name = "centerLongitude")]
    public double? CenterLongitude { get; init; }

    /// <summary>Radius of a circular area of interest in meters.</summary>
    [FromQuery(Name = "radiusMeters")]
    public double? RadiusMeters { get; init; }

    /// <summary>Cross-sensor confidence class: low, nominal, or high.</summary>
    [FromQuery(Name = "confidence")]
    public string? Confidence { get; init; }

    /// <summary>Satellite code: AQUA, TERRA, N, N20, or N21.</summary>
    [FromQuery(Name = "satellite")]
    public string? Satellite { get; init; }

    /// <summary>Inclusive minimum brightness temperature in Kelvin.</summary>
    [FromQuery(Name = "brightnessMin")]
    public double? BrightnessMin { get; init; }

    /// <summary>Inclusive maximum brightness temperature in Kelvin.</summary>
    [FromQuery(Name = "brightnessMax")]
    public double? BrightnessMax { get; init; }

    /// <summary>Inclusive minimum fire radiative power in megawatts.</summary>
    [FromQuery(Name = "frpMin")]
    public double? FrpMin { get; init; }

    /// <summary>Inclusive maximum fire radiative power in megawatts.</summary>
    [FromQuery(Name = "frpMax")]
    public double? FrpMax { get; init; }

    /// <summary>MODIS hotspot type: VEGETATION_FIRE, ACTIVE_VOLCANO, OTHER_STATIC_LAND_SOURCE, or OFFSHORE.</summary>
    [FromQuery(Name = "type")]
    public string? Type { get; init; }

    /// <summary>Number of results to return per page; must be positive and defaults to 100.</summary>
    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; init; } = 100;

    /// <summary>Acquisition timestamp from the previous page's next cursor, including its UTC offset.</summary>
    [FromQuery(Name = "afterAcquiredAt")]
    public DateTimeOffset? AfterAcquiredAt { get; init; }

    /// <summary>Observation ID from the previous page's next cursor.</summary>
    [FromQuery(Name = "afterObservationId")]
    public long? AfterObservationId { get; init; }
}

/// <summary>Cursor used to request the next observation page.</summary>
/// <param name="AcquiredAt">Acquisition timestamp for the final item in the page.</param>
/// <param name="ObservationId">Observation ID for the final item in the page.</param>
public sealed record ObservationCursorResponse(
    DateTimeOffset AcquiredAt,
    long ObservationId);

/// <summary>A page of matching observations and an optional cursor for the next page.</summary>
/// <param name="Items">Observations matching the search filters.</param>
/// <param name="Next">Continuation cursor, or null when no additional matching observations remain.</param>
public sealed record ObservationSearchResponse(
    IReadOnlyList<ObservationResponse> Items,
    ObservationCursorResponse? Next);

/// <summary>Sensor-specific active-fire observation fields exposed by the API.</summary>
/// <param name="ObservationId">Database observation identifier.</param>
/// <param name="Instrument">Sensor instrument: MODIS or VIIRS.</param>
/// <param name="Satellite">Satellite code.</param>
/// <param name="AcquiredAt">Acquisition timestamp in UTC.</param>
/// <param name="Latitude">Latitude of the active-fire pixel center in degrees.</param>
/// <param name="Longitude">Longitude of the active-fire pixel center in degrees.</param>
/// <param name="Brightness">Brightness temperature in Kelvin.</param>
/// <param name="FireRadiativePower">Fire radiative power in megawatts.</param>
/// <param name="Scan">Approximate along-scan pixel size in kilometers.</param>
/// <param name="Track">Approximate along-track pixel size in kilometers.</param>
/// <param name="Confidence">Cross-sensor confidence class.</param>
/// <param name="Type">MODIS-only hotspot type, or null for VIIRS.</param>
public sealed record ObservationResponse(
    long ObservationId,
    string Instrument,
    string Satellite,
    DateTimeOffset AcquiredAt,
    double Latitude,
    double Longitude,
    double Brightness,
    double FireRadiativePower,
    double Scan,
    double Track,
    string Confidence,
    string? Type);
