using System.Globalization;

namespace FireData.Cli;

internal sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

    private Arguments(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {name}");
            }

            if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                flags.Add(name);
                continue;
            }

            values[name] = args[++index];
        }
    }

    public static Arguments Parse(string[] args) => new(args);

    public bool HasFlag(string name) => flags.Contains(name);
    public bool Has(string name) => values.ContainsKey(name);

    public string? Get(string name) => values.GetValueOrDefault(name);

    public string Get(string name, string defaultValue) => Get(name) ?? defaultValue;

    public int GetInt(string name, int defaultValue)
    {
        var value = Get(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"{name} must be an integer.");
        }

        return result;
    }

    public double GetDouble(string name, double? defaultValue = null)
    {
        var value = Get(name);
        if (value is null && defaultValue.HasValue)
        {
            return defaultValue.Value;
        }

        if (value is null ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result))
        {
            throw new ArgumentException($"{name} must be a finite number.");
        }

        return result;
    }

    public string Require(string name)
    {
        var value = Get(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Missing required option {name}.")
            : value;
    }

    public string? GetInstrument()
    {
        var instrument = Get("--instrument");
        if (instrument is null || instrument.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = instrument.ToUpperInvariant();
        return normalized is "MODIS" or "VIIRS"
            ? normalized
            : throw new ArgumentException("--instrument must be MODIS, VIIRS, or all.");
    }

    public string? GetConfidenceClass()
    {
        var value = Get("--confidence-class");
        if (value is null)
        {
            return null;
        }

        var normalized = value.ToLowerInvariant() switch
        {
            "low" => "Low",
            "nominal" or "medium" => "Nominal",
            "high" => "High",
            _ => throw new ArgumentException(
                "--confidence-class must be low, nominal, or high.")
        };
        return normalized;
    }

    public long? GetUtcMicroseconds(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new ArgumentException(
                $"{name} must be an ISO-8601 timestamp or date interpreted as UTC.");
        }

        return SpatialMath.ToUnixMicroseconds(timestamp);
    }
}
