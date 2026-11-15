using Microsoft.EntityFrameworkCore;

namespace FireData.Cli;

public sealed class FireDataContext(DbContextOptions<FireDataContext> options)
    : DbContext(options)
{
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<ModisObservation> ModisObservations => Set<ModisObservation>();
    public DbSet<ViirsObservation> ViirsObservations => Set<ViirsObservation>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<PreprocessingRun> PreprocessingRuns => Set<PreprocessingRun>();
    public DbSet<ClusterMembership> ClusterMemberships => Set<ClusterMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Observation>(entity =>
        {
            entity.ToTable("observations");
            entity.HasKey(x => x.ObservationId);
            entity.Property(x => x.ObservationId).HasColumnName("observation_id");
            entity.Property(x => x.SourceFileId).HasColumnName("source_file_id");
            entity.Property(x => x.SourceRecordNumber).HasColumnName("source_record_number");
            entity.Property(x => x.Instrument).HasColumnName("instrument");
            entity.Property(x => x.Satellite).HasColumnName("satellite");
            entity.Property(x => x.AcquiredAtUtcUs).HasColumnName("acquired_at_utc_us");
            entity.Property(x => x.Latitude).HasColumnName("latitude");
            entity.Property(x => x.Longitude).HasColumnName("longitude");
            entity.Property(x => x.Brightness).HasColumnName("brightness");
            entity.Property(x => x.Scan).HasColumnName("scan");
            entity.Property(x => x.Track).HasColumnName("track");
            entity.Property(x => x.VersionNumber).HasColumnName("version_number");
            entity.Property(x => x.VersionSpecifier).HasColumnName("version_specifier");
            entity.Property(x => x.BrightnessT31).HasColumnName("brightness_t31");
            entity.Property(x => x.FireRadiativePower).HasColumnName("fire_radiative_power");
            entity.Property(x => x.DayNight).HasColumnName("day_night");
            entity.Property(x => x.ModisConfidencePercent).HasColumnName("modis_confidence_percent");
            entity.Property(x => x.ModisHotspotType).HasColumnName("modis_hotspot_type");
            entity.Property(x => x.ViirsConfidence).HasColumnName("viirs_confidence");
            entity.Property(x => x.ConfidenceClass)
                .HasColumnName("confidence_class")
                .HasComputedColumnSql(
                    """
                    CASE instrument
                        WHEN 'MODIS' THEN
                            CASE
                                WHEN modis_confidence_percent <= 33 THEN 'Low'
                                WHEN modis_confidence_percent <= 66 THEN 'Nominal'
                                ELSE 'High'
                            END
                        WHEN 'VIIRS' THEN
                            CASE viirs_confidence
                                WHEN 'LOW' THEN 'Low'
                                WHEN 'MEDIUM' THEN 'Nominal'
                                WHEN 'HIGH' THEN 'High'
                            END
                    END
                    """,
                    stored: true);
            entity.Ignore(x => x.AcquiredAtUtc);
            entity.Ignore(x => x.Frp);
            entity.HasDiscriminator(x => x.Instrument)
                .HasValue<ModisObservation>("MODIS")
                .HasValue<ViirsObservation>("VIIRS");
            entity.HasIndex(x => new { x.Instrument, x.AcquiredAtUtcUs })
                .HasDatabaseName("ix_observations_instrument_time");
            entity.HasIndex(x => new { x.Satellite, x.AcquiredAtUtcUs })
                .HasDatabaseName("ix_observations_satellite_time");
            entity.HasIndex(x => new { x.ConfidenceClass, x.AcquiredAtUtcUs })
                .HasDatabaseName("ix_observations_confidence_time");
        });

        modelBuilder.Entity<SourceFile>(entity =>
        {
            entity.ToTable("source_files");
            entity.HasKey(x => x.SourceFileId);
            entity.Property(x => x.SourceFileId).HasColumnName("source_file_id");
            entity.Property(x => x.SourcePath).HasColumnName("source_path");
            entity.Property(x => x.SourceName).HasColumnName("source_name");
            entity.Property(x => x.Product).HasColumnName("product");
            entity.Property(x => x.Sha256).HasColumnName("sha256");
            entity.Property(x => x.ImportedAtUtcUs).HasColumnName("imported_at_utc_us");
            entity.Property(x => x.ImporterVersion).HasColumnName("importer_version");
            entity.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            entity.Property(x => x.RecordCount).HasColumnName("record_count");
            entity.Property(x => x.AcceptedCount).HasColumnName("accepted_count");
            entity.Property(x => x.ErrorCount).HasColumnName("error_count");
        });

        modelBuilder.Entity<PreprocessingRun>(entity =>
        {
            entity.ToTable("preprocessing_runs");
            entity.HasKey(x => x.PreprocessingRunId);
            entity.Property(x => x.PreprocessingRunId).HasColumnName("preprocessing_run_id");
            entity.Property(x => x.RunKind).HasColumnName("run_kind");
            entity.Property(x => x.AlgorithmVersion).HasColumnName("algorithm_version");
            entity.Property(x => x.CreatedAtUtcUs).HasColumnName("created_at_utc_us");
            entity.Property(x => x.ParametersJson).HasColumnName("parameters_json");
            entity.Property(x => x.OutputSchemaJson).HasColumnName("output_schema_json");
        });

        modelBuilder.Entity<ClusterMembership>(entity =>
        {
            entity.ToTable("cluster_memberships");
            entity.HasKey(x => new { x.PreprocessingRunId, x.ObservationId });
            entity.Property(x => x.PreprocessingRunId).HasColumnName("preprocessing_run_id");
            entity.Property(x => x.ObservationId).HasColumnName("observation_id");
            entity.Property(x => x.ClusterId).HasColumnName("cluster_id");
            entity.Property(x => x.IsCorePoint).HasColumnName("is_core_point");
        });
    }
}
