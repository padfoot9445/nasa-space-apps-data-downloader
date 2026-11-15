use std::path::Path;
use std::time::Instant;

use anyhow::{Context, Result, bail};
use rayon::prelude::*;
use rusqlite::{
    Connection, Transaction, TransactionBehavior, params, params_from_iter, types::Value,
};
use serde_json::json;

use crate::database::open_existing;
use crate::spatial::{distance_meters, envelope_for_radius, to_unix_microseconds};

const MICROSECONDS_PER_DAY: f64 = 86_400_000_000.0;
const EARTH_RADIUS_KM: f64 = 6_371.008_8;
const ALGORITHM_VERSION: &str = "fire-processing-rust-v1";
const SEED_BATCH_SIZE: i64 = 256;
const PROGRESS_INTERVAL: i64 = 1_000;
const QUEUE_BATCH_SIZE: i64 = 256;
const CANDIDATE_BATCH_SIZE: i64 = 512;
const NEIGHBOR_INSERT_BATCH_SIZE: usize = 500;
const PARALLEL_CANDIDATE_THRESHOLD: usize = 256;

#[derive(Debug)]
pub struct ProcessOptions {
    pub grid_degrees: f64,
    pub epsilon_m: f64,
    pub time_scale_m_per_day: f64,
    pub min_points: i32,
    pub instrument: String,
    pub confidence_class: Option<String>,
    pub from: Option<String>,
    pub to: Option<String>,
}

#[derive(Clone, Copy, Debug)]
struct SpatialPoint {
    observation_id: i64,
    acquired_at_utc_us: i64,
    latitude: f64,
    longitude: f64,
}

#[derive(Debug)]
struct Selection {
    sql: String,
    values: Vec<Value>,
}

pub fn run(database_path: &Path, options: ProcessOptions) -> Result<()> {
    validate_options(&options)?;
    if !database_path.is_file() {
        bail!(
            "Database not found: {}. Import data with the Python sqlite_import module first.",
            database_path.display()
        );
    }

    let instrument = normalize_instrument(&options.instrument)?;
    let confidence_class = normalize_confidence(options.confidence_class.as_deref())?;
    let from = options
        .from
        .as_deref()
        .map(to_unix_microseconds)
        .transpose()
        .context("--from must be an ISO-8601 timestamp or date interpreted as UTC")?;
    let to = options
        .to
        .as_deref()
        .map(to_unix_microseconds)
        .transpose()
        .context("--to must be an ISO-8601 timestamp or date interpreted as UTC")?;
    if let (Some(from), Some(to)) = (from, to)
        && from >= to
    {
        bail!("--from must be earlier than --to.");
    }

    let time_window_days = options.epsilon_m / options.time_scale_m_per_day;
    let grid_scheme = format!("latlon-{}-degrees-v1", compact_float(options.grid_degrees));
    let grid_width = checked_grid_dimension(360.0, options.grid_degrees)?;
    let grid_height = checked_grid_dimension(180.0, options.grid_degrees)?;
    let selection = build_selection(instrument.as_deref(), confidence_class.as_deref(), from, to);

    let total_started = Instant::now();
    let staging_started = Instant::now();
    let mut connection = open_existing(database_path)?;
    connection.pragma_update(None, "temp_store", "FILE")?;
    register_sql_functions(&connection, options.grid_degrees)?;
    let transaction = connection.transaction_with_behavior(TransactionBehavior::Immediate)?;
    create_working_tables(&transaction)?;

    let (selected_count, cleaned_count) = count_input_rows(&transaction, &selection)?;
    if selected_count == 0 {
        bail!("No observations match the selected processing input.");
    }
    if cleaned_count == 0 {
        bail!(
            "The selected input contains no observations that pass the processing quality rules."
        );
    }

    let parameters_json = json!({
        "instrument": instrument.as_deref().unwrap_or("all"),
        "confidenceClass": confidence_class,
        "fromUtcUs": from,
        "toUtcUs": to,
        "qualityRules": [
            "scan and track must be positive finite values",
            "brightness and brightness_t31 must be finite",
            "fire_radiative_power must be finite and non-negative",
            "confidence_class must be present"
        ],
        "gridDegrees": options.grid_degrees,
        "gridScheme": grid_scheme,
        "epsilonMeters": options.epsilon_m,
        "timeScaleMetersPerDay": options.time_scale_m_per_day,
        "minPoints": options.min_points,
        "spatiotemporalMetric": "sqrt(haversine_distance_m^2 + (time_delta_days * time_scale_m_per_day)^2)",
        "timeWindowDays": time_window_days,
        "presenceHeuristic": "same-or-adjacent-detection-cell-v1"
    });
    let output_schema_json = json!({
        "observationsTable": "observations",
        "provenanceTable": "processing_run_sources",
        "eventsTable": "processing_events",
        "dailyMetricsTable": "daily_cell_metrics",
        "presenceTable": "daily_cell_presence_heuristic"
    });
    let run_id = create_processing_run(
        &transaction,
        "clean-cluster-aggregate",
        &parameters_json.to_string(),
        &output_schema_json.to_string(),
    )?;
    persist_source_provenance(&transaction, run_id, &selection)?;
    populate_working_points(
        &transaction,
        &selection,
        options.grid_degrees,
        grid_width,
        grid_height,
    )?;
    let staged_count: i64 =
        transaction.query_row("SELECT COUNT(*) FROM temp.processing_points", [], |row| {
            row.get(0)
        })?;
    if staged_count != cleaned_count {
        bail!("Selected {cleaned_count} clean observations, but SQLite staged {staged_count}.");
    }
    create_working_indexes(&transaction)?;

    eprintln!(
        "Input selection and staging completed in {:.2?}.",
        staging_started.elapsed()
    );
    let clustering_started = Instant::now();
    let instruments = {
        let mut statement = transaction.prepare(
            "SELECT DISTINCT instrument FROM temp.processing_points ORDER BY instrument",
        )?;
        statement
            .query_map([], |row| row.get::<_, String>(0))?
            .collect::<rusqlite::Result<Vec<_>>>()?
    };
    let mut cluster_count = 0_i64;
    for instrument_name in instruments {
        cluster_count += cluster_instrument(
            &transaction,
            &instrument_name,
            options.epsilon_m,
            options.time_scale_m_per_day,
            options.min_points,
        )?;
    }
    eprintln!(
        "Clustering completed in {:.2?}.",
        clustering_started.elapsed()
    );
    let noise_count: i64 = transaction.query_row(
        "SELECT COUNT(*) FROM temp.processing_points WHERE cluster_id = -1",
        [],
        |row| row.get(0),
    )?;

    let persistence_started = Instant::now();
    persist_memberships(&transaction, run_id)?;
    persist_events(&transaction, run_id)?;
    persist_daily_metrics(&transaction, run_id, &grid_scheme)?;
    persist_presence(&transaction, run_id, grid_width, grid_height, &grid_scheme)?;
    transaction.commit()?;
    eprintln!(
        "Aggregation and persistence completed in {:.2?}.",
        persistence_started.elapsed()
    );
    eprintln!("Processing completed in {:.2?}.", total_started.elapsed());
    println!(
        "{}",
        json!({
            "preprocessingRunId": run_id,
            "selectedObservations": selected_count,
            "processedObservations": cleaned_count,
            "excludedByQualityRules": selected_count - cleaned_count,
            "clusters": cluster_count,
            "noiseObservations": noise_count,
            "gridScheme": grid_scheme,
            "presenceHeuristic": "same-or-adjacent-detection-cell-v1"
        })
    );
    Ok(())
}

fn validate_options(options: &ProcessOptions) -> Result<()> {
    if !options.grid_degrees.is_finite()
        || !(0.0..=30.0).contains(&options.grid_degrees)
        || options.grid_degrees == 0.0
    {
        bail!("--grid-degrees must be greater than 0 and no greater than 30.");
    }
    if !options.epsilon_m.is_finite()
        || !(0.0..=20_000_000.0).contains(&options.epsilon_m)
        || options.epsilon_m == 0.0
    {
        bail!("--epsilon-m must be greater than 0 and no greater than 20000000.");
    }
    if !options.time_scale_m_per_day.is_finite()
        || !(0.0..=20_000_000.0).contains(&options.time_scale_m_per_day)
        || options.time_scale_m_per_day == 0.0
    {
        bail!("--time-scale-m-per-day must be greater than 0 and no greater than 20000000.");
    }
    if options.min_points < 1 {
        bail!("--min-points must be at least one.");
    }
    let window = options.epsilon_m / options.time_scale_m_per_day * MICROSECONDS_PER_DAY;
    if !window.is_finite() || window > i64::MAX as f64 {
        bail!("The selected time scale produces an unbounded neighborhood window.");
    }
    Ok(())
}

fn checked_grid_dimension(extent: f64, grid_degrees: f64) -> Result<i64> {
    let dimension = (extent / grid_degrees).ceil();
    if !dimension.is_finite() || dimension > i64::MAX as f64 {
        bail!("--grid-degrees is too small to represent the requested grid.");
    }
    Ok(dimension as i64)
}

fn normalize_instrument(instrument: &str) -> Result<Option<String>> {
    match instrument.to_ascii_lowercase().as_str() {
        "all" => Ok(None),
        "modis" => Ok(Some("MODIS".to_owned())),
        "viirs" => Ok(Some("VIIRS".to_owned())),
        _ => bail!("--instrument must be MODIS, VIIRS, or all."),
    }
}

fn normalize_confidence(value: Option<&str>) -> Result<Option<String>> {
    match value.map(str::to_ascii_lowercase).as_deref() {
        None => Ok(None),
        Some("low") => Ok(Some("Low".to_owned())),
        Some("nominal" | "medium") => Ok(Some("Nominal".to_owned())),
        Some("high") => Ok(Some("High".to_owned())),
        _ => bail!("--confidence-class must be low, nominal, or high."),
    }
}

fn build_selection(
    instrument: Option<&str>,
    confidence: Option<&str>,
    from: Option<i64>,
    to: Option<i64>,
) -> Selection {
    let mut clauses = Vec::new();
    let mut values = Vec::new();
    if let Some(instrument) = instrument {
        clauses.push("instrument = ?".to_owned());
        values.push(Value::Text(instrument.to_owned()));
    }
    if let Some(confidence) = confidence {
        clauses.push("confidence_class = ?".to_owned());
        values.push(Value::Text(confidence.to_owned()));
    }
    if let Some(from) = from {
        clauses.push("acquired_at_utc_us >= ?".to_owned());
        values.push(Value::Integer(from));
    }
    if let Some(to) = to {
        clauses.push("acquired_at_utc_us < ?".to_owned());
        values.push(Value::Integer(to));
    }
    Selection {
        sql: if clauses.is_empty() {
            "1 = 1".to_owned()
        } else {
            clauses.join(" AND ")
        },
        values,
    }
}

const QUALITY_SQL: &str = "scan > 0 AND scan <= 1.0e308 \
    AND track > 0 AND track <= 1.0e308 \
    AND brightness BETWEEN -1.0e308 AND 1.0e308 \
    AND brightness_t31 BETWEEN -1.0e308 AND 1.0e308 \
    AND fire_radiative_power BETWEEN 0 AND 1.0e308 \
    AND confidence_class IS NOT NULL";

fn count_input_rows(transaction: &Transaction<'_>, selection: &Selection) -> Result<(i64, i64)> {
    let selected = transaction.query_row(
        &format!("SELECT COUNT(*) FROM observations WHERE {}", selection.sql),
        params_from_iter(selection.values.iter()),
        |row| row.get(0),
    )?;
    let cleaned = transaction.query_row(
        &format!(
            "SELECT COUNT(*) FROM observations WHERE {} AND {QUALITY_SQL}",
            selection.sql
        ),
        params_from_iter(selection.values.iter()),
        |row| row.get(0),
    )?;
    Ok((selected, cleaned))
}

fn create_working_tables(transaction: &Transaction<'_>) -> Result<()> {
    transaction.execute_batch(
        "
        CREATE TEMP TABLE processing_points (
            observation_id INTEGER PRIMARY KEY,
            instrument TEXT NOT NULL,
            acquired_at_utc_us INTEGER NOT NULL,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            frp REAL NOT NULL,
            utc_date TEXT NOT NULL,
            cell_x INTEGER NOT NULL,
            cell_y INTEGER NOT NULL,
            visited INTEGER NOT NULL DEFAULT 0,
            cluster_id INTEGER NOT NULL DEFAULT 0,
            is_core_point INTEGER NOT NULL DEFAULT 0
        );
        CREATE TEMP TABLE processing_queue (
            queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
            observation_id INTEGER NOT NULL UNIQUE,
            processed INTEGER NOT NULL DEFAULT 0
        );
        CREATE TEMP TABLE processing_neighbors (observation_id INTEGER PRIMARY KEY);
        CREATE TEMP TABLE processing_core_points (observation_id INTEGER PRIMARY KEY);
        CREATE TEMP TABLE processing_frontier (
            queue_id INTEGER PRIMARY KEY,
            observation_id INTEGER NOT NULL,
            acquired_at_utc_us INTEGER NOT NULL,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            was_visited INTEGER NOT NULL,
            previous_cluster_id INTEGER NOT NULL
        );
        ",
    )?;
    Ok(())
}

fn create_working_indexes(transaction: &Transaction<'_>) -> Result<()> {
    transaction.execute_batch(
        "
        CREATE INDEX temp.ix_processing_points_instrument_id
            ON processing_points (instrument, observation_id);
        CREATE INDEX temp.ix_processing_points_grid
            ON processing_points (instrument, utc_date, cell_x, cell_y);
        ",
    )?;
    Ok(())
}

fn create_processing_run(
    transaction: &Transaction<'_>,
    run_kind: &str,
    parameters_json: &str,
    output_schema_json: &str,
) -> Result<i64> {
    transaction.execute(
        "
        INSERT INTO preprocessing_runs (
            run_kind, algorithm_version, created_at_utc_us, parameters_json, output_schema_json
        ) VALUES (?1, ?2, ?3, ?4, ?5)
        ",
        params![
            run_kind,
            ALGORITHM_VERSION,
            chrono::Utc::now().timestamp_micros(),
            parameters_json,
            output_schema_json
        ],
    )?;
    Ok(transaction.last_insert_rowid())
}

fn persist_source_provenance(
    transaction: &Transaction<'_>,
    run_id: i64,
    selection: &Selection,
) -> Result<()> {
    let sql = format!(
        "
        INSERT INTO processing_run_sources (
            preprocessing_run_id, source_file_id, source_sha256,
            selected_observation_count, processed_observation_count
        )
        SELECT ?1, o.source_file_id, s.sha256, COUNT(*),
               SUM(CASE WHEN {QUALITY_SQL} THEN 1 ELSE 0 END)
        FROM observations AS o
        JOIN source_files AS s ON s.source_file_id = o.source_file_id
        WHERE {}
        GROUP BY o.source_file_id, s.sha256
        ",
        selection.sql
    );
    let values = std::iter::once(Value::Integer(run_id))
        .chain(selection.values.iter().cloned())
        .collect::<Vec<_>>();
    transaction.execute(&sql, params_from_iter(values.iter()))?;
    Ok(())
}

fn populate_working_points(
    transaction: &Transaction<'_>,
    selection: &Selection,
    grid_degrees: f64,
    grid_width: i64,
    grid_height: i64,
) -> Result<()> {
    let sql = format!(
        "
        INSERT INTO temp.processing_points (
            observation_id, instrument, acquired_at_utc_us, latitude, longitude,
            frp, utc_date, cell_x, cell_y
        )
        SELECT observation_id, instrument, acquired_at_utc_us, latitude, longitude,
               fire_radiative_power,
               date(acquired_at_utc_us / 1000000.0, 'unixepoch'),
               min(CAST((longitude + 180.0) / ? AS INTEGER), ?),
               min(CAST((latitude + 90.0) / ? AS INTEGER), ?)
        FROM observations
        WHERE {} AND {QUALITY_SQL}
        ",
        selection.sql
    );
    let mut values = vec![
        Value::Real(grid_degrees),
        Value::Integer(grid_width - 1),
        Value::Real(grid_degrees),
        Value::Integer(grid_height - 1),
    ];
    values.extend(selection.values.iter().cloned());
    transaction.execute(&sql, params_from_iter(values.iter()))?;
    Ok(())
}

fn cluster_instrument(
    transaction: &Transaction<'_>,
    instrument: &str,
    epsilon_m: f64,
    time_scale_m_per_day: f64,
    min_points: i32,
) -> Result<i64> {
    eprintln!("Clustering {instrument} observations...");
    let started = Instant::now();
    let mut cluster_count = 0_i64;
    let mut scanned = 0_i64;
    let mut next_progress = PROGRESS_INTERVAL;
    let mut last_id = i64::MIN;
    loop {
        let seeds = {
            let mut statement = transaction.prepare(
                "
                SELECT observation_id, acquired_at_utc_us, latitude, longitude
                FROM temp.processing_points
                WHERE instrument = ?1 AND observation_id > ?2
                ORDER BY observation_id LIMIT ?3
                ",
            )?;
            statement
                .query_map(
                    params![instrument, last_id, SEED_BATCH_SIZE],
                    point_from_row,
                )?
                .collect::<rusqlite::Result<Vec<_>>>()?
        };
        if seeds.is_empty() {
            break;
        }
        last_id = seeds.last().expect("non-empty batch").observation_id;
        for seed in seeds {
            scanned += 1;
            if scanned >= next_progress {
                eprintln!(
                    "  {instrument}: scanned {scanned} candidates, {cluster_count} clusters, {:.2?} elapsed",
                    started.elapsed()
                );
                next_progress += PROGRESS_INTERVAL;
            }
            let claimed = transaction.execute(
                "
                UPDATE temp.processing_points
                SET visited = 1, cluster_id = -1
                WHERE observation_id = ?1 AND visited = 0
                ",
                [seed.observation_id],
            )?;
            if claimed == 0 {
                continue;
            }

            transaction.execute("DELETE FROM temp.processing_queue", [])?;
            let candidate_cluster = cluster_count + 1;
            let is_core = expand_point(
                transaction,
                instrument,
                seed,
                candidate_cluster,
                epsilon_m,
                time_scale_m_per_day,
                min_points,
            )?;
            if !is_core {
                continue;
            }
            cluster_count = candidate_cluster;
            while expand_queue_batch(
                transaction,
                instrument,
                cluster_count,
                epsilon_m,
                time_scale_m_per_day,
                min_points,
            )? {}
        }
    }
    eprintln!(
        "  {instrument}: complete; {scanned} candidates, {cluster_count} clusters, {:.2?} elapsed",
        started.elapsed()
    );
    Ok(cluster_count)
}

fn point_from_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<SpatialPoint> {
    Ok(SpatialPoint {
        observation_id: row.get(0)?,
        acquired_at_utc_us: row.get(1)?,
        latitude: row.get(2)?,
        longitude: row.get(3)?,
    })
}

fn expand_point(
    transaction: &Transaction<'_>,
    instrument: &str,
    center: SpatialPoint,
    cluster_id: i64,
    epsilon_m: f64,
    time_scale_m_per_day: f64,
    min_points: i32,
) -> Result<bool> {
    let envelope = envelope_for_radius(center.latitude, center.longitude, epsilon_m)?;
    let window = (epsilon_m / time_scale_m_per_day * MICROSECONDS_PER_DAY).ceil() as i64;
    let min_time = center.acquired_at_utc_us.saturating_sub(window);
    let max_time = center.acquired_at_utc_us.saturating_add(window);
    transaction.execute("DELETE FROM temp.processing_neighbors", [])?;

    let interval_a = envelope.longitude_intervals[0];
    let interval_b =
        envelope
            .longitude_intervals
            .get(1)
            .copied()
            .unwrap_or(crate::spatial::LongitudeInterval {
                west: 0.0,
                east: 0.0,
            });
    let mut neighbor_ids = Vec::new();
    {
        let mut statement = transaction.prepare(
            "
            SELECT p.observation_id, p.acquired_at_utc_us, p.latitude, p.longitude
            FROM observation_rtree AS r
            JOIN temp.processing_points AS p ON p.observation_id = r.observation_id
            WHERE p.instrument = ?1
              AND p.acquired_at_utc_us BETWEEN ?2 AND ?3
              AND r.min_latitude <= ?4 AND r.max_latitude >= ?5
              AND ((r.min_longitude <= ?6 AND r.max_longitude >= ?7)
                   OR (?8 = 1 AND r.min_longitude <= ?9 AND r.max_longitude >= ?10))
            ",
        )?;
        let rows = statement.query_map(
            params![
                instrument,
                min_time,
                max_time,
                envelope.north,
                envelope.south,
                interval_a.east,
                interval_a.west,
                i64::from(envelope.longitude_intervals.len() > 1),
                interval_b.east,
                interval_b.west
            ],
            point_from_row,
        )?;
        let mut candidates = Vec::with_capacity(CANDIDATE_BATCH_SIZE as usize);
        for row in rows {
            candidates.push(row?);
            if candidates.len() == CANDIDATE_BATCH_SIZE as usize {
                neighbor_ids.extend(find_neighbors(
                    center,
                    &candidates,
                    epsilon_m,
                    time_scale_m_per_day,
                ));
                candidates.clear();
            }
        }
        if !candidates.is_empty() {
            neighbor_ids.extend(find_neighbors(
                center,
                &candidates,
                epsilon_m,
                time_scale_m_per_day,
            ));
        }
    }
    insert_neighbor_batch(transaction, &neighbor_ids)?;

    transaction.execute(
        "
        INSERT OR IGNORE INTO temp.processing_core_points (observation_id)
        SELECT ?1 WHERE (SELECT COUNT(*) FROM temp.processing_neighbors) >= ?2
        ",
        params![center.observation_id, i64::from(min_points)],
    )?;
    transaction.execute(
        "
        INSERT OR IGNORE INTO temp.processing_queue (observation_id)
        SELECT observation_id FROM temp.processing_neighbors
        WHERE observation_id <> ?1
          AND EXISTS (
              SELECT 1 FROM temp.processing_core_points WHERE observation_id = ?1
          )
        ",
        [center.observation_id],
    )?;
    transaction.execute(
        "
        UPDATE temp.processing_points
        SET cluster_id = ?2, is_core_point = 1
        WHERE observation_id = ?1
          AND EXISTS (
              SELECT 1 FROM temp.processing_core_points WHERE observation_id = ?1
          )
        ",
        params![center.observation_id, cluster_id],
    )?;
    let is_core: i64 = transaction.query_row(
        "SELECT EXISTS(SELECT 1 FROM temp.processing_core_points WHERE observation_id = ?1)",
        [center.observation_id],
        |row| row.get(0),
    )?;
    Ok(is_core != 0)
}

fn find_neighbors(
    center: SpatialPoint,
    candidates: &[SpatialPoint],
    epsilon_m: f64,
    time_scale_m_per_day: f64,
) -> Vec<i64> {
    if candidates.len() >= PARALLEL_CANDIDATE_THRESHOLD {
        candidates
            .par_iter()
            .filter(|candidate| is_neighbor(center, **candidate, epsilon_m, time_scale_m_per_day))
            .map(|candidate| candidate.observation_id)
            .collect()
    } else {
        candidates
            .iter()
            .filter(|candidate| is_neighbor(center, **candidate, epsilon_m, time_scale_m_per_day))
            .map(|candidate| candidate.observation_id)
            .collect()
    }
}

fn is_neighbor(
    center: SpatialPoint,
    candidate: SpatialPoint,
    epsilon_m: f64,
    time_scale_m_per_day: f64,
) -> bool {
    let distance = distance_meters(
        center.latitude,
        center.longitude,
        candidate.latitude,
        candidate.longitude,
    );
    let elapsed_days = ((candidate.acquired_at_utc_us as f64 - center.acquired_at_utc_us as f64)
        / MICROSECONDS_PER_DAY)
        .abs();
    let temporal_distance = elapsed_days * time_scale_m_per_day;
    distance.powi(2) + temporal_distance.powi(2) <= epsilon_m.powi(2)
}

fn insert_neighbor_batch(transaction: &Transaction<'_>, ids: &[i64]) -> Result<()> {
    for chunk in ids.chunks(NEIGHBOR_INSERT_BATCH_SIZE) {
        let placeholders = std::iter::repeat_n("(?)", chunk.len())
            .collect::<Vec<_>>()
            .join(",");
        let sql = format!(
            "INSERT OR IGNORE INTO temp.processing_neighbors (observation_id) VALUES {placeholders}"
        );
        transaction.execute(&sql, params_from_iter(chunk.iter()))?;
    }
    Ok(())
}

fn expand_queue_batch(
    transaction: &Transaction<'_>,
    instrument: &str,
    cluster_id: i64,
    epsilon_m: f64,
    time_scale_m_per_day: f64,
    min_points: i32,
) -> Result<bool> {
    transaction.execute("DELETE FROM temp.processing_frontier", [])?;
    transaction.execute(
        "
        INSERT INTO temp.processing_frontier (
            queue_id, observation_id, acquired_at_utc_us, latitude, longitude,
            was_visited, previous_cluster_id
        )
        SELECT q.queue_id, p.observation_id, p.acquired_at_utc_us, p.latitude,
               p.longitude, p.visited, p.cluster_id
        FROM temp.processing_queue AS q
        JOIN temp.processing_points AS p ON p.observation_id = q.observation_id
        WHERE p.instrument = ?1 AND q.processed = 0
        ORDER BY q.queue_id LIMIT ?2
        ",
        params![instrument, QUEUE_BATCH_SIZE],
    )?;
    let count: i64 =
        transaction.query_row("SELECT COUNT(*) FROM temp.processing_frontier", [], |row| {
            row.get(0)
        })?;
    if count == 0 {
        transaction.execute("DELETE FROM temp.processing_queue", [])?;
        return Ok(false);
    }
    transaction.execute(
        "
        UPDATE temp.processing_queue SET processed = 1
        WHERE queue_id IN (SELECT queue_id FROM temp.processing_frontier)
        ",
        [],
    )?;
    transaction.execute(
        "
        UPDATE temp.processing_points SET cluster_id = ?1
        WHERE observation_id IN (SELECT observation_id FROM temp.processing_frontier)
          AND cluster_id <= 0
        ",
        [cluster_id],
    )?;
    transaction.execute(
        "
        UPDATE temp.processing_points SET visited = 1
        WHERE observation_id IN (SELECT observation_id FROM temp.processing_frontier)
          AND visited = 0
        ",
        [],
    )?;
    let frontier = {
        let mut statement = transaction.prepare(
            "
            SELECT observation_id, acquired_at_utc_us, latitude, longitude, was_visited
            FROM temp.processing_frontier ORDER BY queue_id
            ",
        )?;
        statement
            .query_map([], |row| {
                Ok((
                    SpatialPoint {
                        observation_id: row.get(0)?,
                        acquired_at_utc_us: row.get(1)?,
                        latitude: row.get(2)?,
                        longitude: row.get(3)?,
                    },
                    row.get::<_, i64>(4)? != 0,
                ))
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?
    };
    for (point, was_visited) in frontier {
        if !was_visited {
            expand_point(
                transaction,
                instrument,
                point,
                cluster_id,
                epsilon_m,
                time_scale_m_per_day,
                min_points,
            )?;
        }
    }
    Ok(true)
}

fn persist_memberships(transaction: &Transaction<'_>, run_id: i64) -> Result<()> {
    transaction.execute(
        "
        INSERT INTO cluster_memberships (
            preprocessing_run_id, observation_id, cluster_id, is_core_point
        )
        SELECT ?1, observation_id, CASE WHEN cluster_id > 0 THEN cluster_id ELSE NULL END,
               is_core_point
        FROM temp.processing_points
        ",
        [run_id],
    )?;
    Ok(())
}

fn persist_events(transaction: &Transaction<'_>, run_id: i64) -> Result<()> {
    transaction.execute(
        "
        INSERT INTO processing_events (
            preprocessing_run_id, instrument, cluster_id, observation_count,
            start_at_utc_us, end_at_utc_us, centroid_latitude, centroid_longitude,
            frp_sum, frp_max
        )
        SELECT ?1, p.instrument, m.cluster_id, COUNT(*),
               MIN(p.acquired_at_utc_us), MAX(p.acquired_at_utc_us),
               AVG(p.latitude),
               atan2(AVG(sin(radians(p.longitude))), AVG(cos(radians(p.longitude))))
                   * 180.0 / pi(),
               SUM(p.frp), MAX(p.frp)
        FROM temp.processing_points AS p
        JOIN cluster_memberships AS m
          ON m.preprocessing_run_id = ?1 AND m.observation_id = p.observation_id
        WHERE m.cluster_id IS NOT NULL
        GROUP BY p.instrument, m.cluster_id
        ",
        [run_id],
    )?;
    Ok(())
}

fn persist_daily_metrics(
    transaction: &Transaction<'_>,
    run_id: i64,
    grid_scheme: &str,
) -> Result<()> {
    transaction.execute(
        "
        INSERT INTO daily_cell_metrics (
            preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y, instrument,
            detection_count, event_count, frp_sum, frp_max, cell_area_km2
        )
        SELECT ?1, p.utc_date, ?2, p.cell_x, p.cell_y, p.instrument,
               COUNT(*), COUNT(DISTINCT m.cluster_id), SUM(p.frp), MAX(p.frp),
               cell_area_km2(p.cell_x, p.cell_y)
        FROM temp.processing_points AS p
        LEFT JOIN cluster_memberships AS m
          ON m.preprocessing_run_id = ?1 AND m.observation_id = p.observation_id
        GROUP BY p.utc_date, p.cell_x, p.cell_y, p.instrument
        ",
        params![run_id, grid_scheme],
    )?;
    Ok(())
}

fn persist_presence(
    transaction: &Transaction<'_>,
    run_id: i64,
    grid_width: i64,
    grid_height: i64,
    grid_scheme: &str,
) -> Result<()> {
    transaction.execute(
        "
        WITH offsets(dx, dy) AS (
            VALUES (-1, -1), (0, -1), (1, -1),
                   (-1, 0),  (0, 0),  (1, 0),
                   (-1, 1),  (0, 1),  (1, 1)
        ),
        candidates AS (
            SELECT d.utc_date, d.instrument,
                   (d.cell_x + offsets.dx + ?3) % ?3 AS cell_x,
                   d.cell_y + offsets.dy AS cell_y,
                   d.cell_x AS source_x, d.cell_y AS source_y
            FROM daily_cell_metrics AS d
            CROSS JOIN offsets
            WHERE d.preprocessing_run_id = ?1
              AND d.cell_y + offsets.dy BETWEEN 0 AND ?4
        )
        INSERT INTO daily_cell_presence_heuristic (
            preprocessing_run_id, utc_date, grid_scheme, cell_x, cell_y,
            instrument, has_direct_detection, neighboring_detection_cells, method
        )
        SELECT ?1, c.utc_date, ?2, c.cell_x, c.cell_y, c.instrument,
               MAX(CASE WHEN c.cell_x = c.source_x AND c.cell_y = c.source_y THEN 1 ELSE 0 END),
               COUNT(*), 'same-or-adjacent-detection-cell-v1'
        FROM candidates AS c
        GROUP BY c.utc_date, c.instrument, c.cell_x, c.cell_y
        ",
        params![run_id, grid_scheme, grid_width, grid_height - 1],
    )?;
    Ok(())
}

fn register_sql_functions(connection: &Connection, grid_degrees: f64) -> Result<()> {
    use rusqlite::functions::FunctionFlags;
    let flags = FunctionFlags::SQLITE_DETERMINISTIC | FunctionFlags::SQLITE_INNOCUOUS;
    connection.create_scalar_function("radians", 1, flags, |ctx| {
        Ok(ctx.get::<f64>(0)?.to_radians())
    })?;
    connection.create_scalar_function("sin", 1, flags, |ctx| Ok(ctx.get::<f64>(0)?.sin()))?;
    connection.create_scalar_function("cos", 1, flags, |ctx| Ok(ctx.get::<f64>(0)?.cos()))?;
    connection.create_scalar_function("atan2", 2, flags, |ctx| {
        Ok(ctx.get::<f64>(0)?.atan2(ctx.get::<f64>(1)?))
    })?;
    connection.create_scalar_function("pi", 0, flags, |_| Ok(std::f64::consts::PI))?;
    connection.create_scalar_function("cell_area_km2", 2, flags, move |ctx| {
        let cell_x = ctx.get::<i64>(0)?;
        let cell_y = ctx.get::<i64>(1)?;
        let south = (-90.0_f64).max(-90.0 + cell_y as f64 * grid_degrees);
        let north = 90.0_f64.min(south + grid_degrees);
        let west = -180.0 + cell_x as f64 * grid_degrees;
        let width = grid_degrees.min(180.0 - west);
        Ok(EARTH_RADIUS_KM
            * EARTH_RADIUS_KM
            * width.to_radians()
            * (north.to_radians().sin() - south.to_radians().sin()))
    })?;
    Ok(())
}

fn compact_float(value: f64) -> String {
    let mut formatted = format!("{value:.8}");
    while formatted.contains('.') && formatted.ends_with('0') {
        formatted.pop();
    }
    if formatted.ends_with('.') {
        formatted.pop();
    }
    formatted
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusqlite::Connection;
    use tempfile::tempdir;

    #[test]
    fn confidence_and_instrument_are_normalized() {
        assert_eq!(normalize_instrument("all").unwrap(), None);
        assert_eq!(
            normalize_instrument("modis").unwrap().as_deref(),
            Some("MODIS")
        );
        assert_eq!(
            normalize_confidence(Some("medium")).unwrap().as_deref(),
            Some("Nominal")
        );
    }

    #[test]
    fn grid_scheme_matches_csharp_precision() {
        assert_eq!(compact_float(0.25), "0.25");
        assert_eq!(compact_float(0.123456789), "0.12345679");
    }

    #[test]
    fn processes_and_calibrates_temporary_schema_v2_database() {
        let directory = tempdir().unwrap();
        let database_path = directory.path().join("fixture.sqlite");
        create_calibration_fixture(&database_path);

        run(
            &database_path,
            ProcessOptions {
                grid_degrees: 7.0,
                epsilon_m: 2_000.0,
                time_scale_m_per_day: 1_500.0,
                min_points: 2,
                instrument: "all".to_owned(),
                confidence_class: None,
                from: None,
                to: None,
            },
        )
        .unwrap();

        let connection = Connection::open(&database_path).unwrap();
        let memberships: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id IS NOT NULL",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let events: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM processing_events WHERE preprocessing_run_id = 1",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let daily_metrics: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM daily_cell_metrics WHERE preprocessing_run_id = 1",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(memberships, 18);
        assert_eq!(events, 6);
        assert_eq!(daily_metrics, 6);
        let provenance: (i64, i64) = connection
            .query_row(
                "
                SELECT selected_observation_count, processed_observation_count
                FROM processing_run_sources WHERE preprocessing_run_id = 1
                ",
                [],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .unwrap();
        assert_eq!(provenance, (19, 18));
        drop(connection);

        crate::calibration::run(&database_path, 1, "ratio").unwrap();
        crate::calibration::run(&database_path, 1, "poisson").unwrap();

        let connection = Connection::open(&database_path).unwrap();
        let models: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM calibration_models WHERE preprocessing_run_id = 1",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let calibrated: i64 = connection
            .query_row("SELECT COUNT(*) FROM calibrated_daily_metrics", [], |row| {
                row.get(0)
            })
            .unwrap();
        let knots: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM calibration_frp_quantiles",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(models, 2);
        assert_eq!(calibrated, 12);
        assert_eq!(knots, 6);
    }

    #[test]
    fn parallel_candidate_batches_keep_dense_neighborhood_as_noise() {
        let directory = tempdir().unwrap();
        let database_path = directory.path().join("dense.sqlite");
        let connection = create_database_with_source(&database_path, 520);
        let acquired_at = chrono::NaiveDate::from_ymd_opt(2024, 6, 1)
            .unwrap()
            .and_hms_opt(12, 0, 0)
            .unwrap()
            .and_utc()
            .timestamp_micros();
        for record in 1..=520 {
            insert_observation(
                &connection,
                record,
                "MODIS",
                if record % 2 == 0 { "AQUA" } else { "TERRA" },
                acquired_at,
                37.0,
                -119.9,
                10.0,
                Some(80),
                None,
            );
        }
        drop(connection);

        run(
            &database_path,
            ProcessOptions {
                grid_degrees: 0.25,
                epsilon_m: 2_000.0,
                time_scale_m_per_day: 1_500.0,
                min_points: 521,
                instrument: "MODIS".to_owned(),
                confidence_class: None,
                from: None,
                to: None,
            },
        )
        .unwrap();

        let connection = Connection::open(&database_path).unwrap();
        let noise: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id IS NULL",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(noise, 520);
    }

    #[test]
    fn clusters_across_antimeridian_and_uses_circular_centroid() {
        let directory = tempdir().unwrap();
        let database_path = directory.path().join("antimeridian.sqlite");
        let connection = create_database_with_source(&database_path, 2);
        let acquired_at = chrono::NaiveDate::from_ymd_opt(2024, 6, 1)
            .unwrap()
            .and_hms_opt(12, 0, 0)
            .unwrap()
            .and_utc()
            .timestamp_micros();
        insert_observation(
            &connection,
            1,
            "MODIS",
            "AQUA",
            acquired_at,
            37.0,
            179.999,
            1.0,
            Some(80),
            None,
        );
        insert_observation(
            &connection,
            2,
            "MODIS",
            "AQUA",
            acquired_at,
            37.0,
            -179.999,
            1.0,
            Some(80),
            None,
        );
        drop(connection);

        run(
            &database_path,
            ProcessOptions {
                grid_degrees: 7.0,
                epsilon_m: 500.0,
                time_scale_m_per_day: 1_500.0,
                min_points: 2,
                instrument: "MODIS".to_owned(),
                confidence_class: None,
                from: None,
                to: None,
            },
        )
        .unwrap();

        let connection = Connection::open(&database_path).unwrap();
        let event: (i64, f64) = connection
            .query_row(
                "
                SELECT observation_count, centroid_longitude
                FROM processing_events WHERE preprocessing_run_id = 1
                ",
                [],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .unwrap();
        assert_eq!(event.0, 2);
        assert!((event.1.abs() - 180.0).abs() < 0.01);
    }

    #[test]
    fn previously_noisy_point_can_become_a_border_member() {
        let directory = tempdir().unwrap();
        let database_path = directory.path().join("border.sqlite");
        let connection = create_database_with_source(&database_path, 5);
        let acquired_at = chrono::NaiveDate::from_ymd_opt(2024, 6, 1)
            .unwrap()
            .and_hms_opt(12, 0, 0)
            .unwrap()
            .and_utc()
            .timestamp_micros();
        for (record, longitude) in [(1, 0.0012), (2, -0.0004), (3, 0.0), (4, 0.0004), (5, 1.0)] {
            insert_observation(
                &connection,
                record,
                "MODIS",
                "AQUA",
                acquired_at,
                if record == 5 { 38.0 } else { 37.0 },
                longitude,
                1.0,
                Some(80),
                None,
            );
        }
        drop(connection);

        run(
            &database_path,
            ProcessOptions {
                grid_degrees: 7.0,
                epsilon_m: 100.0,
                time_scale_m_per_day: 1_500.0,
                min_points: 3,
                instrument: "MODIS".to_owned(),
                confidence_class: None,
                from: None,
                to: None,
            },
        )
        .unwrap();

        let connection = Connection::open(&database_path).unwrap();
        let clustered: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id > 0",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let noise: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id IS NULL",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let border: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM cluster_memberships WHERE preprocessing_run_id = 1 AND cluster_id > 0 AND is_core_point = 0",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(clustered, 4);
        assert_eq!(noise, 1);
        assert_eq!(border, 1);
    }

    fn create_calibration_fixture(path: &Path) {
        let connection = create_database_with_source(path, 19);
        let mut record = 0_i64;
        for (year, point_count) in [(2020, 2_i64), (2021, 3), (2022, 4)] {
            let acquired_at = chrono::NaiveDate::from_ymd_opt(year, 6, 1)
                .unwrap()
                .and_hms_opt(12, 0, 0)
                .unwrap()
                .and_utc()
                .timestamp_micros();
            for instrument in ["MODIS", "VIIRS"] {
                for index in 0..point_count {
                    record += 1;
                    let is_modis = instrument == "MODIS";
                    insert_observation(
                        &connection,
                        record,
                        instrument,
                        if is_modis { "AQUA" } else { "N" },
                        acquired_at + index * 1_000_000,
                        37.0,
                        -119.9,
                        10.0 + index as f64 + if is_modis { 0.0 } else { 5.0 },
                        if is_modis { Some(80) } else { None },
                        if is_modis { None } else { Some("HIGH") },
                    );
                }
            }
        }
        connection
            .execute(
                "
                INSERT INTO observations (
                    source_file_id, source_record_number, instrument, satellite,
                    acquired_at_utc_us, latitude, longitude, brightness, scan,
                    track, version_number, version_specifier, brightness_t31,
                    fire_radiative_power, day_night, modis_confidence_percent
                ) VALUES (1, 19, 'MODIS', 'AQUA', 0, 37, -119.9, 320, 0,
                          1, 1, 'NRT', 300, 10, 'D', 80)
                ",
                [],
            )
            .unwrap();
    }

    fn create_database_with_source(path: &Path, record_count: i64) -> Connection {
        let connection = Connection::open(path).unwrap();
        connection
            .execute_batch(include_str!("../../sqlite/schema.sql"))
            .unwrap();
        connection
            .execute(
                "
                INSERT INTO source_files (
                    source_path, source_name, product, sha256, imported_at_utc_us,
                    importer_version, schema_version, record_count, accepted_count, error_count
                ) VALUES ('fixture', 'fixture.json', 'test', 'fixture', 0, 'test', 2, ?1, ?1, 0)
                ",
                [record_count],
            )
            .unwrap();
        connection
    }

    #[allow(clippy::too_many_arguments)]
    fn insert_observation(
        connection: &Connection,
        record: i64,
        instrument: &str,
        satellite: &str,
        acquired_at: i64,
        latitude: f64,
        longitude: f64,
        frp: f64,
        modis_confidence: Option<i64>,
        viirs_confidence: Option<&str>,
    ) {
        connection
            .execute(
                "
                INSERT INTO observations (
                    source_file_id, source_record_number, instrument, satellite,
                    acquired_at_utc_us, latitude, longitude, brightness, scan,
                    track, version_number, version_specifier, brightness_t31,
                    fire_radiative_power, day_night, modis_confidence_percent,
                    viirs_confidence
                ) VALUES (
                    1, ?1, ?2, ?3, ?4, ?5, ?6, 320, 1, 1, 1, 'NRT',
                    300, ?7, 'D', ?8, ?9
                )
                ",
                rusqlite::params![
                    record,
                    instrument,
                    satellite,
                    acquired_at,
                    latitude,
                    longitude,
                    frp,
                    modis_confidence,
                    viirs_confidence
                ],
            )
            .unwrap();
    }
}
