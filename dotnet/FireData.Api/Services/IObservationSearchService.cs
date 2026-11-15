using FireData.Api.Models;

namespace FireData.Api.Services;

/// <summary>Queries persisted observations using validated API search filters.</summary>
public interface IObservationSearchService
{
    /// <summary>Returns a page of matching observations.</summary>
    /// <param name="request">Filters, page size, and optional continuation cursor.</param>
    /// <param name="cancellationToken">Token used to cancel database work.</param>
    /// <returns>The matching observation page and its optional next cursor.</returns>
    Task<ObservationSearchResponse> SearchAsync(
        ObservationSearchRequest request,
        CancellationToken cancellationToken);
}
