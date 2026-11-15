namespace FireData.Cli;

internal sealed record CalibrationPair(
    int Year,
    double ModisCount,
    double ViirsCount,
    double ModisFrp,
    double ViirsFrp);

internal interface ICountCalibrationStrategy
{
    string Name { get; }
    double[] Fit(IReadOnlyList<CalibrationPair> pairs);
    double Predict(double sourceCount, IReadOnlyList<double> parameters);
}

internal interface IFrpCalibrationStrategy
{
    string Name { get; }
    FrpMapping Fit(IReadOnlyList<CalibrationPair> pairs);
    double Map(double sourceFrp, FrpMapping mapping);
}

internal sealed record FrpMapping(double[] SourceQuantiles, double[] ReferenceQuantiles);

internal static class CalibrationStrategyRegistry
{
    private static readonly IReadOnlyDictionary<string, ICountCalibrationStrategy> CountStrategies =
        new Dictionary<string, ICountCalibrationStrategy>(StringComparer.OrdinalIgnoreCase)
        {
            ["ratio"] = new RatioCountCalibrationStrategy(),
            ["poisson"] = new PoissonCountCalibrationStrategy()
        };

    public static IFrpCalibrationStrategy Frp { get; } = new EmpiricalQuantileMappingStrategy();

    public static ICountCalibrationStrategy GetCount(string name) =>
        CountStrategies.TryGetValue(name, out var strategy)
            ? strategy
            : throw new ArgumentException("--count-method must be ratio or poisson.");
}

internal sealed class RatioCountCalibrationStrategy : ICountCalibrationStrategy
{
    public string Name => "ratio";

    public double[] Fit(IReadOnlyList<CalibrationPair> pairs)
    {
        var sourceTotal = pairs.Sum(x => x.ModisCount);
        if (sourceTotal <= 0)
        {
            throw new InvalidOperationException(
                "Cannot fit ratio calibration because the MODIS training count total is zero.");
        }

        return [pairs.Sum(x => x.ViirsCount) / sourceTotal];
    }

    public double Predict(double sourceCount, IReadOnlyList<double> parameters) =>
        sourceCount * parameters[0];
}

internal sealed class PoissonCountCalibrationStrategy : ICountCalibrationStrategy
{
    public string Name => "poisson";

    public double[] Fit(IReadOnlyList<CalibrationPair> pairs)
    {
        if (pairs.Count < 2)
        {
            throw new InvalidOperationException(
                "Poisson calibration requires at least two paired daily-cell observations.");
        }

        var features = pairs.Select(x => Math.Log(1 + x.ModisCount)).ToArray();
        if (features.Max() - features.Min() < 1e-10)
        {
            throw new InvalidOperationException(
                "Poisson calibration requires variation in paired MODIS counts.");
        }

        var mean = pairs.Average(x => x.ViirsCount);
        var intercept = Math.Log(Math.Max(mean, 1e-8));
        var slope = 0.0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var s00 = 0.0;
            var s01 = 0.0;
            var s11 = 0.0;
            var t0 = 0.0;
            var t1 = 0.0;
            for (var index = 0; index < pairs.Count; index++)
            {
                var x = features[index];
                var eta = Math.Clamp(intercept + slope * x, -30, 30);
                var mu = Math.Exp(eta);
                var response = eta + (pairs[index].ViirsCount - mu) / mu;
                s00 += mu;
                s01 += mu * x;
                s11 += mu * x * x;
                t0 += mu * response;
                t1 += mu * x * response;
            }

            var determinant = s00 * s11 - s01 * s01;
            if (!double.IsFinite(determinant) || determinant <= 1e-16)
            {
                throw new InvalidOperationException(
                    "Poisson calibration could not estimate a stable two-parameter model.");
            }

            var nextIntercept = (t0 * s11 - t1 * s01) / determinant;
            var nextSlope = (t1 * s00 - t0 * s01) / determinant;
            if (!double.IsFinite(nextIntercept) || !double.IsFinite(nextSlope))
            {
                throw new InvalidOperationException(
                    "Poisson calibration produced non-finite coefficients.");
            }

            var step = Math.Max(Math.Abs(nextIntercept - intercept), Math.Abs(nextSlope - slope));
            if (step > 5)
            {
                var scale = 5 / step;
                nextIntercept = intercept + (nextIntercept - intercept) * scale;
                nextSlope = slope + (nextSlope - slope) * scale;
                step = 5;
            }

            intercept = nextIntercept;
            slope = nextSlope;
            if (step < 1e-8)
            {
                return [intercept, slope];
            }
        }

        throw new InvalidOperationException(
            "Poisson calibration did not converge within 100 IRLS iterations.");
    }

    public double Predict(double sourceCount, IReadOnlyList<double> parameters)
    {
        var value = Math.Exp(Math.Clamp(
            parameters[0] + parameters[1] * Math.Log(1 + sourceCount),
            -30,
            30));
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException("Poisson calibration predicted a non-finite count.");
        }

        return value;
    }
}

internal sealed class EmpiricalQuantileMappingStrategy : IFrpCalibrationStrategy
{
    public string Name => "empirical-quantile-mapping";

    public FrpMapping Fit(IReadOnlyList<CalibrationPair> pairs)
    {
        if (pairs.Count < 2)
        {
            throw new InvalidOperationException(
                "FRP quantile mapping requires at least two paired daily-cell observations.");
        }

        var source = pairs.Select(x => x.ModisFrp).Order().ToArray();
        var reference = pairs.Select(x => x.ViirsFrp).Order().ToArray();
        return new FrpMapping(source, reference);
    }

    public double Map(double sourceFrp, FrpMapping mapping)
    {
        if (sourceFrp <= 0)
        {
            return 0;
        }

        var lower = LowerBound(mapping.SourceQuantiles, sourceFrp);
        var upper = UpperBound(mapping.SourceQuantiles, sourceFrp);
        var rank = lower == upper
            ? Math.Clamp(lower / (double)Math.Max(1, mapping.SourceQuantiles.Length - 1), 0, 1)
            : Math.Clamp(
                (lower + upper - 1) /
                (2.0 * Math.Max(1, mapping.SourceQuantiles.Length - 1)),
                0,
                1);
        var position = rank * (mapping.ReferenceQuantiles.Length - 1);
        var lowIndex = (int)Math.Floor(position);
        var highIndex = Math.Min(lowIndex + 1, mapping.ReferenceQuantiles.Length - 1);
        var fraction = position - lowIndex;
        return mapping.ReferenceQuantiles[lowIndex] * (1 - fraction) +
            mapping.ReferenceQuantiles[highIndex] * fraction;
    }

    private static int LowerBound(IReadOnlyList<double> values, double target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int UpperBound(IReadOnlyList<double> values, double target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] <= target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
