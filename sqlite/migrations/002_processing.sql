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
