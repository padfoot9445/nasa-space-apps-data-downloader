using FireData.Api.Models;
using FireData.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FireData.Api.Endpoints;

/// <summary>Maps active-fire observation search endpoints.</summary>
public static class ObservationEndpoints
{
    /// <summary>Registers the observation search route.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns>The same endpoint route builder for further configuration.</returns>
    public static IEndpointRouteBuilder MapObservationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/observations", SearchAsync)
            .WithName("SearchObservations")
            .WithTags("Observations")
            .WithSummary("Search MODIS and VIIRS active-fire observations")
            .WithDescription(
                "Filters observations by acquisition date/time, UTC time of day, geographic area, " +
                "confidence class, satellite, brightness, FRP, and MODIS hotspot type. Results are " +
                "ordered by acquisition time and observation ID and use a continuation cursor.")
            .Produces<ObservationSearchResponse>()
            .ProducesValidationProblem();

        endpoints.MapGet("/api/observations/timeseries", GetTimeSeriesAsync)
            .WithName("GetObservationTimeSeries")
            .WithTags("Observations")
            .WithSummary("Get daily MODIS and VIIRS detection counts")
            .WithDescription(
                "Returns daily UTC observation counts using the same date, time, area, and " +
                "sensor-property filters as the observation search endpoint. These are " +
                "detections, not estimates of unique fire events.")
            .Produces<IReadOnlyList<ObservationTimeSeriesPoint>>()
            .ProducesValidationProblem();

        return endpoints;
    }

    private static async Task<Results<Ok<IReadOnlyList<ObservationTimeSeriesPoint>>, ValidationProblem>>
        GetTimeSeriesAsync(
            [AsParameters] ObservationSearchRequest request,
            HttpRequest httpRequest,
            IObservationTimeSeriesService service,
            CancellationToken cancellationToken)
    {
        var errors = ObservationSearchRequestValidator.Validate(request, httpRequest.Query);
        if (errors.Count != 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var response = await service.GetAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<ObservationSearchResponse>, ValidationProblem>> SearchAsync(
        [AsParameters] ObservationSearchRequest request,
        HttpRequest httpRequest,
        IObservationSearchService service,
        CancellationToken cancellationToken)
    {
        var errors = ObservationSearchRequestValidator.Validate(request, httpRequest.Query);
        if (errors.Count != 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var response = await service.SearchAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }
}
