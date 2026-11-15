using FireData.Api.Models;
using Microsoft.AspNetCore.Http;

namespace FireData.Api.Services;

internal static class ObservationSearchRequestValidator
{
    private static readonly HashSet<string> ConfidenceClasses =
        new(["LOW", "NOMINAL", "HIGH"], StringComparer.Ordinal);

    private static readonly HashSet<string> Satellites =
        new(["AQUA", "TERRA", "N", "N20", "N21"], StringComparer.Ordinal);

    private static readonly HashSet<string> ModisTypes =
        new(
            ["VEGETATION_FIRE", "ACTIVE_VOLCANO", "OTHER_STATIC_LAND_SOURCE", "OFFSHORE"],
            StringComparer.Ordinal);

    public static Dictionary<string, string[]> Validate(
        ObservationSearchRequest request,
        IQueryCollection query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.PageSize is < 1 or > 500)
        {
            AddError(errors, nameof(request.PageSize), "pageSize must be between 1 and 500.");
        }

        if (request.DateFrom > request.DateTo)
        {
            AddError(errors, nameof(request.DateFrom), "dateFrom must be no later than dateTo.");
        }

        if (request.AcquiredFrom > request.AcquiredTo)
        {
            AddError(
                errors,
                nameof(request.AcquiredFrom),
                "acquiredFrom must be earlier than or equal to acquiredTo.");
        }

        ValidateTimestampOffset(query, "acquiredFrom", errors);
        ValidateTimestampOffset(query, "acquiredTo", errors);
        ValidateTimestampOffset(query, "afterAcquiredAt", errors);

        if (request.TimeOfDayFrom > request.TimeOfDayTo &&
            (request.TimeOfDayFrom is null || request.TimeOfDayTo is null))
        {
            AddError(
                errors,
                nameof(request.TimeOfDayTo),
                "Both time-of-day bounds are required when the range crosses midnight.");
        }

        ValidateBoundingBox(request, errors);
        ValidateRadius(request, errors);

        if (HasAnyBoundingBoxValue(request) && HasAnyRadiusValue(request))
        {
            AddError(errors, "areaOfInterest", "Specify either a bounding box or a radius, not both.");
        }

        if (request.Confidence is not null &&
            !ConfidenceClasses.Contains(request.Confidence.ToUpperInvariant()))
        {
            AddError(errors, nameof(request.Confidence), "confidence must be low, nominal, or high.");
        }

        if (request.Satellite is not null &&
            !Satellites.Contains(request.Satellite.ToUpperInvariant()))
        {
            AddError(
                errors,
                nameof(request.Satellite),
                "satellite must be AQUA, TERRA, N, N20, or N21.");
        }

        if (request.Type is not null &&
            !ModisTypes.Contains(request.Type.ToUpperInvariant()))
        {
            AddError(
                errors,
                nameof(request.Type),
                "type must be VEGETATION_FIRE, ACTIVE_VOLCANO, OTHER_STATIC_LAND_SOURCE, or OFFSHORE.");
        }

        ValidateRange(
            errors,
            nameof(request.BrightnessMin),
            request.BrightnessMin,
            nameof(request.BrightnessMax),
            request.BrightnessMax);
        ValidateRange(
            errors,
            nameof(request.FrpMin),
            request.FrpMin,
            nameof(request.FrpMax),
            request.FrpMax);

        if (request.BrightnessMin is < 0 || request.BrightnessMax is < 0)
        {
            AddError(errors, nameof(request.BrightnessMin), "Brightness bounds cannot be negative.");
        }

        if (request.FrpMin is < 0 || request.FrpMax is < 0)
        {
            AddError(errors, nameof(request.FrpMin), "FRP bounds cannot be negative.");
        }

        if (request.AfterObservationId is <= 0)
        {
            AddError(errors, nameof(request.AfterObservationId), "afterObservationId must be positive.");
        }

        if (request.AfterAcquiredAt.HasValue != request.AfterObservationId.HasValue)
        {
            AddError(
                errors,
                "cursor",
                "afterAcquiredAt and afterObservationId must be supplied together.");
        }

        return errors;
    }

    private static void ValidateBoundingBox(
        ObservationSearchRequest request,
        IDictionary<string, string[]> errors)
    {
        if (!HasAnyBoundingBoxValue(request))
        {
            return;
        }

        if (request.West is null || request.South is null ||
            request.East is null || request.North is null)
        {
            AddError(
                errors,
                "boundingBox",
                "west, south, east, and north must all be supplied.");
            return;
        }

        if (!InRange(request.West.Value, -180, 180) ||
            !InRange(request.East.Value, -180, 180) ||
            !InRange(request.South.Value, -90, 90) ||
            !InRange(request.North.Value, -90, 90) ||
            request.South > request.North)
        {
            AddError(
                errors,
                "boundingBox",
                "Bounding-box coordinates are out of range or south is greater than north.");
        }
    }

    private static void ValidateRadius(
        ObservationSearchRequest request,
        IDictionary<string, string[]> errors)
    {
        if (!HasAnyRadiusValue(request))
        {
            return;
        }

        if (request.CenterLatitude is null ||
            request.CenterLongitude is null ||
            request.RadiusMeters is null)
        {
            AddError(
                errors,
                "radius",
                "centerLatitude, centerLongitude, and radiusMeters must all be supplied.");
            return;
        }

        if (!InRange(request.CenterLatitude.Value, -90, 90) ||
            !InRange(request.CenterLongitude.Value, -180, 180) ||
            !double.IsFinite(request.RadiusMeters.Value) ||
            request.RadiusMeters < 0 ||
            request.RadiusMeters > 20_015_114)
        {
            AddError(
                errors,
                "radius",
                "Radius center coordinates are out of range or radiusMeters must be in [0, 20015114].");
        }
    }

    private static void ValidateRange(
        IDictionary<string, string[]> errors,
        string minimumName,
        double? minimum,
        string maximumName,
        double? maximum)
    {
        if (minimum is { } min && !double.IsFinite(min))
        {
            AddError(errors, minimumName, $"{minimumName} must be finite.");
        }

        if (maximum is { } max && !double.IsFinite(max))
        {
            AddError(errors, maximumName, $"{maximumName} must be finite.");
        }

        if (minimum > maximum)
        {
            AddError(errors, minimumName, $"{minimumName} cannot exceed {maximumName}.");
        }
    }

    private static bool HasAnyBoundingBoxValue(ObservationSearchRequest request) =>
        request.West.HasValue ||
        request.South.HasValue ||
        request.East.HasValue ||
        request.North.HasValue;

    private static bool HasAnyRadiusValue(ObservationSearchRequest request) =>
        request.CenterLatitude.HasValue ||
        request.CenterLongitude.HasValue ||
        request.RadiusMeters.HasValue;

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static void ValidateTimestampOffset(
        IQueryCollection query,
        string parameterName,
        IDictionary<string, string[]> errors)
    {
        var value = query[parameterName].ToString();
        if (value.Length == 0)
        {
            return;
        }

        var hasZuluSuffix = value.EndsWith('Z') || value.EndsWith('z');
        var hasNumericOffset =
            value.Length >= 6 &&
            value[^3] == ':' &&
            value[^6] is '+' or '-';
        if (!hasZuluSuffix && !hasNumericOffset)
        {
            AddError(
                errors,
                parameterName,
                $"{parameterName} must include an explicit UTC offset, such as Z or +00:00.");
        }
    }

    private static void AddError(
        IDictionary<string, string[]> errors,
        string field,
        string message) =>
        errors[field] = errors.TryGetValue(field, out var messages)
            ? [.. messages, message]
            : [message];
}
