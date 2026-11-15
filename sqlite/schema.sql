PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS source_files (
    source_file_id INTEGER PRIMARY KEY,
    source_path TEXT NOT NULL,
    source_name TEXT NOT NULL,
    product TEXT,
    sha256 TEXT,
    imported_at_utc_us INTEGER NOT NULL,
    importer_version TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    record_count INTEGER NOT NULL DEFAULT 0 CHECK (record_count >= 0),
    accepted_count INTEGER NOT NULL DEFAULT 0 CHECK (accepted_count >= 0),
    error_count INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0)
);

CREATE TABLE IF NOT EXISTS observations (
    observation_id INTEGER PRIMARY KEY,
    source_file_id INTEGER NOT NULL REFERENCES source_files(source_file_id),
    source_record_number INTEGER NOT NULL CHECK (source_record_number > 0),
    instrument TEXT NOT NULL CHECK (instrument IN ('MODIS', 'VIIRS')),
    satellite TEXT NOT NULL,
    acquired_at_utc_us INTEGER NOT NULL,
    latitude REAL NOT NULL CHECK (latitude BETWEEN -90.0 AND 90.0),
    longitude REAL NOT NULL CHECK (longitude BETWEEN -180.0 AND 180.0),
    brightness REAL NOT NULL,
    scan REAL NOT NULL,
    track REAL NOT NULL,
    version_number REAL NOT NULL,
    version_specifier TEXT,
    brightness_t31 REAL NOT NULL,
    fire_radiative_power REAL NOT NULL,
    day_night TEXT NOT NULL CHECK (day_night IN ('D', 'N')),
    modis_confidence_percent INTEGER CHECK (
        modis_confidence_percent IS NULL OR modis_confidence_percent BETWEEN 0 AND 100
    ),
    modis_hotspot_type TEXT CHECK (
        modis_hotspot_type IS NULL OR modis_hotspot_type IN (
            'VEGETATION_FIRE',
            'ACTIVE_VOLCANO',
            'OTHER_STATIC_LAND_SOURCE',
            'OFFSHORE'
        )
    ),
    viirs_confidence TEXT CHECK (
        viirs_confidence IS NULL OR viirs_confidence IN ('LOW', 'MEDIUM', 'HIGH')
    ),
    confidence_class TEXT GENERATED ALWAYS AS (
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
    ) STORED,
    UNIQUE (source_file_id, source_record_number),
    CHECK (
        (instrument = 'MODIS'
            AND satellite IN ('AQUA', 'TERRA')
            AND modis_confidence_percent IS NOT NULL
            AND viirs_confidence IS NULL)
        OR
        (instrument = 'VIIRS'
            AND satellite IN ('N', 'N20', 'N21')
            AND modis_confidence_percent IS NULL
            AND modis_hotspot_type IS NULL
            AND viirs_confidence IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_observations_instrument_time
    ON observations (instrument, acquired_at_utc_us);
CREATE INDEX IF NOT EXISTS ix_observations_satellite_time
    ON observations (satellite, acquired_at_utc_us);
CREATE INDEX IF NOT EXISTS ix_observations_confidence_time
    ON observations (confidence_class, acquired_at_utc_us);

CREATE VIRTUAL TABLE IF NOT EXISTS observation_rtree USING rtree(
    observation_id,
    min_longitude,
    max_longitude,
    min_latitude,
    max_latitude
);

CREATE TRIGGER IF NOT EXISTS observations_rtree_insert
AFTER INSERT ON observations
BEGIN
    INSERT OR REPLACE INTO observation_rtree (
        observation_id, min_longitude, max_longitude, min_latitude, max_latitude
    ) VALUES (
        NEW.observation_id, NEW.longitude, NEW.longitude, NEW.latitude, NEW.latitude
    );
END;

CREATE TRIGGER IF NOT EXISTS observations_rtree_update
AFTER UPDATE OF longitude, latitude ON observations
BEGIN
    UPDATE observation_rtree
    SET min_longitude = NEW.longitude,
        max_longitude = NEW.longitude,
        min_latitude = NEW.latitude,
        max_latitude = NEW.latitude
    WHERE observation_id = NEW.observation_id;
END;

CREATE TRIGGER IF NOT EXISTS observations_rtree_delete
AFTER DELETE ON observations
BEGIN
    DELETE FROM observation_rtree WHERE observation_id = OLD.observation_id;
END;

CREATE TABLE IF NOT EXISTS preprocessing_runs (
    preprocessing_run_id INTEGER PRIMARY KEY,
    run_kind TEXT NOT NULL,
    algorithm_version TEXT NOT NULL,
    created_at_utc_us INTEGER NOT NULL,
    parameters_json TEXT NOT NULL,
    output_schema_json TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS harmonized_observations (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    observation_id INTEGER NOT NULL
        REFERENCES observations(observation_id) ON DELETE CASCADE,
    payload_json TEXT NOT NULL,
    PRIMARY KEY (preprocessing_run_id, observation_id)
);

CREATE TABLE IF NOT EXISTS cluster_memberships (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    observation_id INTEGER NOT NULL
        REFERENCES observations(observation_id) ON DELETE CASCADE,
    cluster_id INTEGER,
    is_core_point INTEGER NOT NULL CHECK (is_core_point IN (0, 1)),
    PRIMARY KEY (preprocessing_run_id, observation_id)
);
CREATE INDEX IF NOT EXISTS ix_cluster_memberships_cluster
    ON cluster_memberships (preprocessing_run_id, cluster_id);

CREATE TABLE IF NOT EXISTS processing_events (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    instrument TEXT NOT NULL CHECK (instrument IN ('MODIS', 'VIIRS')),
    cluster_id INTEGER NOT NULL CHECK (cluster_id > 0),
    observation_count INTEGER NOT NULL CHECK (observation_count > 0),
    start_at_utc_us INTEGER NOT NULL,
    end_at_utc_us INTEGER NOT NULL,
    centroid_latitude REAL NOT NULL CHECK (centroid_latitude BETWEEN -90 AND 90),
    centroid_longitude REAL NOT NULL CHECK (centroid_longitude BETWEEN -180 AND 180),
    frp_sum REAL NOT NULL CHECK (frp_sum >= 0),
    frp_max REAL NOT NULL CHECK (frp_max >= 0),
    PRIMARY KEY (preprocessing_run_id, instrument, cluster_id),
    CHECK (start_at_utc_us <= end_at_utc_us)
);

CREATE INDEX IF NOT EXISTS ix_processing_events_time
    ON processing_events (preprocessing_run_id, instrument, start_at_utc_us);

CREATE TABLE IF NOT EXISTS processing_run_sources (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    source_file_id INTEGER NOT NULL REFERENCES source_files(source_file_id),
    source_sha256 TEXT,
    selected_observation_count INTEGER NOT NULL CHECK (selected_observation_count > 0),
    processed_observation_count INTEGER NOT NULL
        CHECK (processed_observation_count BETWEEN 0 AND selected_observation_count),
    PRIMARY KEY (preprocessing_run_id, source_file_id)
);

CREATE TABLE IF NOT EXISTS daily_cell_metrics (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    utc_date TEXT NOT NULL CHECK (length(utc_date) = 10),
    grid_scheme TEXT NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    instrument TEXT NOT NULL CHECK (instrument IN ('MODIS', 'VIIRS')),
    detection_count INTEGER NOT NULL CHECK (detection_count > 0),
    event_count INTEGER NOT NULL CHECK (event_count >= 0),
    frp_sum REAL NOT NULL CHECK (frp_sum >= 0),
    frp_max REAL NOT NULL CHECK (frp_max >= 0),
    cell_area_km2 REAL NOT NULL CHECK (cell_area_km2 > 0),
    PRIMARY KEY (
        preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y, instrument
    )
);

CREATE INDEX IF NOT EXISTS ix_daily_cell_metrics_sensor_date
    ON daily_cell_metrics (preprocessing_run_id, instrument, utc_date);

CREATE TABLE IF NOT EXISTS daily_cell_presence_heuristic (
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    utc_date TEXT NOT NULL CHECK (length(utc_date) = 10),
    grid_scheme TEXT NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    instrument TEXT NOT NULL CHECK (instrument IN ('MODIS', 'VIIRS')),
    has_direct_detection INTEGER NOT NULL CHECK (has_direct_detection IN (0, 1)),
    neighboring_detection_cells INTEGER NOT NULL CHECK (neighboring_detection_cells > 0),
    method TEXT NOT NULL,
    PRIMARY KEY (
        preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y, instrument
    )
);

CREATE TABLE IF NOT EXISTS calibration_models (
    calibration_model_id INTEGER PRIMARY KEY,
    preprocessing_run_id INTEGER NOT NULL
        REFERENCES preprocessing_runs(preprocessing_run_id) ON DELETE CASCADE,
    count_method TEXT NOT NULL CHECK (count_method IN ('ratio', 'poisson')),
    frp_method TEXT NOT NULL CHECK (frp_method = 'empirical-quantile-mapping'),
    reference_sensor TEXT NOT NULL CHECK (reference_sensor = 'VIIRS'),
    algorithm_version TEXT NOT NULL,
    parameters_json TEXT NOT NULL,
    evaluation_json TEXT NOT NULL,
    created_at_utc_us INTEGER NOT NULL,
    UNIQUE (preprocessing_run_id, count_method, frp_method)
);

CREATE TABLE IF NOT EXISTS calibrated_daily_metrics (
    calibration_model_id INTEGER NOT NULL
        REFERENCES calibration_models(calibration_model_id) ON DELETE CASCADE,
    utc_date TEXT NOT NULL CHECK (length(utc_date) = 10),
    grid_scheme TEXT NOT NULL,
    cell_x INTEGER NOT NULL,
    cell_y INTEGER NOT NULL,
    instrument TEXT NOT NULL CHECK (instrument IN ('MODIS', 'VIIRS')),
    source_detection_count INTEGER NOT NULL CHECK (source_detection_count > 0),
    calibrated_detection_count REAL NOT NULL CHECK (calibrated_detection_count >= 0),
    count_lower REAL NOT NULL CHECK (count_lower >= 0),
    count_upper REAL NOT NULL CHECK (count_upper >= count_lower),
    source_frp_sum REAL NOT NULL CHECK (source_frp_sum >= 0),
    calibrated_frp_sum REAL NOT NULL CHECK (calibrated_frp_sum >= 0),
    frp_lower REAL NOT NULL CHECK (frp_lower >= 0),
    frp_upper REAL NOT NULL CHECK (frp_upper >= frp_lower),
    PRIMARY KEY (
        calibration_model_id, utc_date, grid_scheme, cell_x, cell_y, instrument
    )
);

CREATE TABLE IF NOT EXISTS calibration_frp_quantiles (
    calibration_model_id INTEGER NOT NULL
        REFERENCES calibration_models(calibration_model_id) ON DELETE CASCADE,
    knot_index INTEGER NOT NULL CHECK (knot_index >= 0),
    modis_frp REAL NOT NULL CHECK (modis_frp >= 0),
    viirs_frp REAL NOT NULL CHECK (viirs_frp >= 0),
    PRIMARY KEY (calibration_model_id, knot_index)
);

CREATE INDEX IF NOT EXISTS ix_calibrated_daily_metrics_sensor_date
    ON calibrated_daily_metrics (calibration_model_id, instrument, utc_date);

PRAGMA user_version = 2;
