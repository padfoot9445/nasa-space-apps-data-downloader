using System.ComponentModel.DataAnnotations.Schema;

namespace FireData.Cli;

public abstract class Observation
{
    public long ObservationId { get; private set; }
    public long SourceFileId { get; private set; }
    public long SourceRecordNumber { get; private set; }
    public string Instrument { get; private set; } = null!;
    public string Satellite { get; private set; } = null!;
    public long AcquiredAtUtcUs { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double Brightness { get; private set; }
    public double Scan { get; private set; }
    public double Track { get; private set; }
    public double VersionNumber { get; private set; }
    public string? VersionSpecifier { get; private set; }
    public double BrightnessT31 { get; private set; }
    public double FireRadiativePower { get; private set; }
    public string DayNight { get; private set; } = null!;
    public int? ModisConfidencePercent { get; private set; }
    public string? ModisHotspotType { get; private set; }
    public string? ViirsConfidence { get; private set; }
    public string ConfidenceClass { get; private set; } = null!;

    [NotMapped]
    public DateTimeOffset AcquiredAtUtc =>
        DateTimeOffset.UnixEpoch.AddTicks(checked(AcquiredAtUtcUs * 10));

    [NotMapped]
    public double Frp => FireRadiativePower;
}

public sealed class ModisObservation : Observation
{
    [NotMapped]
    public ModisSatellite SatelliteType =>
        Enum.Parse<ModisSatellite>(Satellite, ignoreCase: false);

    [NotMapped]
    public ModisConfidence? Confidence =>
        ModisConfidencePercent switch
        {
            null => null,
            <= 33 => ModisConfidence.Low,
            <= 66 => ModisConfidence.Nominal,
            _ => ModisConfidence.High,
        };
}

public sealed class ViirsObservation : Observation
{
    [NotMapped]
    public ViirsSatellite SatelliteType =>
        Enum.Parse<ViirsSatellite>(Satellite, ignoreCase: false);

    [NotMapped]
    public ViirsConfidence? Confidence =>
        ViirsConfidence is null
            ? null
            : Enum.Parse<ViirsConfidence>(ViirsConfidence, ignoreCase: false);
}

public enum ModisSatellite
{
    AQUA,
    TERRA
}

public enum ViirsSatellite
{
    N,
    N20,
    N21
}

public enum ModisConfidence
{
    Low,
    Nominal,
    High
}

public enum ViirsConfidence
{
    LOW,
    MEDIUM,
    HIGH
}

public sealed class SourceFile
{
    public long SourceFileId { get; set; }
    public string SourcePath { get; set; } = null!;
    public string SourceName { get; set; } = null!;
    public string? Product { get; set; }
    public string? Sha256 { get; set; }
    public long ImportedAtUtcUs { get; set; }
    public string ImporterVersion { get; set; } = null!;
    public int SchemaVersion { get; set; }
    public long RecordCount { get; set; }
    public long AcceptedCount { get; set; }
    public long ErrorCount { get; set; }
}

public sealed class PreprocessingRun
{
    public long PreprocessingRunId { get; set; }
    public string RunKind { get; set; } = null!;
    public string AlgorithmVersion { get; set; } = null!;
    public long CreatedAtUtcUs { get; set; }
    public string ParametersJson { get; set; } = null!;
    public string OutputSchemaJson { get; set; } = "{}";
}

public sealed class ClusterMembership
{
    public long PreprocessingRunId { get; set; }
    public long ObservationId { get; set; }
    public int? ClusterId { get; set; }
    public bool IsCorePoint { get; set; }
}

public sealed record ObservationOutput(
    long ObservationId,
    string Instrument,
    string Satellite,
    DateTimeOffset AcquiredAtUtc,
    double Latitude,
    double Longitude,
    double Brightness,
    double BrightnessT31,
    double FireRadiativePower,
    double Scan,
    double Track,
    string DayNight,
    double VersionNumber,
    string? VersionSpecifier,
    int? ModisConfidencePercent,
    string? ModisHotspotType,
    string? ViirsConfidence,
    string ConfidenceClass);
