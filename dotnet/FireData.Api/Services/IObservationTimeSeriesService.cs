using FireData.Api.Models;

namespace FireData.Api.Services;

/// <summary>Creates daily detection-count time series from observation filters.</summary>
public interface IObservationTimeSeriesService
{
    /// <summary>Returns daily MODIS and VIIRS counts matching the supplied filters.</summary>
    Task<IReadOnlyList<ObservationTimeSeriesPoint>> GetAsync(
        ObservationSearchRequest request,
        CancellationToken cancellationToken);
}
