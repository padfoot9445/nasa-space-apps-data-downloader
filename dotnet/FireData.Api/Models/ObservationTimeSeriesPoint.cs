namespace FireData.Api.Models;

/// <summary>Daily MODIS and VIIRS detection counts for a UTC date.</summary>
/// <param name="Date">UTC date of the observations.</param>
/// <param name="ModisCount">Number of matching MODIS observations.</param>
/// <param name="ViirsCount">Number of matching VIIRS observations.</param>
public sealed record ObservationTimeSeriesPoint(
    DateOnly Date,
    long ModisCount,
    long ViirsCount);
