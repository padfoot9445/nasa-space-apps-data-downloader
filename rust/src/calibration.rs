use std::collections::BTreeMap;
use std::path::Path;

use anyhow::{Context, Result, bail};
use rusqlite::{Transaction, TransactionBehavior, params};
use serde_json::json;

use crate::database::open_existing;

const ALGORITHM_VERSION: &str = "sensor-calibration-rust-v1";

#[derive(Clone, Debug)]
struct DailyMetric {
    utc_date: String,
    grid_scheme: String,
    cell_x: i64,
    cell_y: i64,
    instrument: String,
    detection_count: i64,
    frp_sum: f64,
}

#[derive(Clone, Copy, Debug)]
struct CalibrationPair {
    year: i32,
    modis_count: f64,
    viirs_count: f64,
    modis_frp: f64,
    viirs_frp: f64,
}

#[derive(Debug)]
struct FrpMapping {
    source_quantiles: Vec<f64>,
    reference_quantiles: Vec<f64>,
}

struct CalibrationOutput<'a> {
    count_method: &'a str,
    count_parameters: &'a [f64],
    count_error_p90: f64,
    frp_mapping: &'a FrpMapping,
    frp_error_p90: f64,
}

pub fn run(database_path: &Path, run_id: i64, count_method: &str) -> Result<()> {
    if run_id < 1 {
        bail!("--run-id must be a positive integer.");
    }
    let method = match count_method.to_ascii_lowercase().as_str() {
        "ratio" => "ratio",
        "poisson" => "poisson",
        _ => bail!("--count-method must be ratio or poisson."),
    };

    let mut connection = open_existing(database_path)?;
    let transaction = connection.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let metrics = read_daily_metrics(&transaction, run_id)?;
    if metrics.is_empty() {
        bail!("Processing run {run_id} has no daily-cell metrics; run process first.");
    }
    let pairs = pair_metrics(&metrics)?;
    if pairs.len() < 2 {
        bail!("Calibration requires at least two same-day, same-cell MODIS/VIIRS metric pairs.");
    }
    let years = pairs
        .iter()
        .map(|pair| pair.year)
        .collect::<std::collections::BTreeSet<_>>();
    if years.len() < 2 {
        bail!(
            "Year-blocked calibration requires paired observations spanning at least two UTC years."
        );
    }

    let mut count_fold_errors = Vec::new();
    let mut count_fold_squared_errors = Vec::new();
    let mut frp_fold_errors = Vec::new();
    let mut fold_evaluations = Vec::new();
    for held_out_year in &years {
        let training = pairs
            .iter()
            .copied()
            .filter(|pair| pair.year != *held_out_year)
            .collect::<Vec<_>>();
        let holdout = pairs
            .iter()
            .copied()
            .filter(|pair| pair.year == *held_out_year)
            .collect::<Vec<_>>();
        if training.len() < 2 || holdout.is_empty() {
            bail!(
                "Year-blocked calibration fold {held_out_year} has insufficient paired training or holdout data."
            );
        }

        let count_parameters = fit_count(method, &training).with_context(|| {
            format!("Year-blocked calibration fold {held_out_year} is not valid")
        })?;
        let frp_mapping = fit_frp(&training).with_context(|| {
            format!("Year-blocked calibration fold {held_out_year} is not valid")
        })?;
        let mut fold_count_errors = Vec::with_capacity(holdout.len());
        let mut fold_count_squared_errors = Vec::with_capacity(holdout.len());
        let mut fold_frp_errors = Vec::with_capacity(holdout.len());
        for pair in &holdout {
            let count_error =
                predict_count(method, pair.modis_count, &count_parameters) - pair.viirs_count;
            let abs_count_error = count_error.abs();
            count_fold_errors.push(abs_count_error);
            count_fold_squared_errors.push(count_error * count_error);
            fold_count_errors.push(abs_count_error);
            fold_count_squared_errors.push(count_error * count_error);
            let frp_error = map_frp(pair.modis_frp, &frp_mapping) - pair.viirs_frp;
            let abs_frp_error = frp_error.abs();
            frp_fold_errors.push(abs_frp_error);
            fold_frp_errors.push(abs_frp_error);
        }
        fold_evaluations.push(json!({
            "heldOutYear": held_out_year,
            "pairedDailyCells": holdout.len(),
            "countMae": mean(&fold_count_errors),
            "countRmse": mean(&fold_count_squared_errors).sqrt(),
            "frpMae": mean(&fold_frp_errors)
        }));
    }

    let all_pairs = pairs.as_slice();
    let count_parameters = fit_count(method, all_pairs)?;
    let frp_mapping = fit_frp(all_pairs)?;
    let count_error_p90 = quantile(&count_fold_errors, 0.9)?;
    let frp_error_p90 = quantile(&frp_fold_errors, 0.9)?;
    let count_rmse = mean(&count_fold_squared_errors).sqrt();
    let evaluation = json!({
        "validation": "leave-one-utc-year-out",
        "pairedDailyCells": pairs.len(),
        "heldOutYears": years.iter().copied().collect::<Vec<_>>(),
        "countMae": mean(&count_fold_errors),
        "countRmse": count_rmse,
        "countAbsoluteErrorP90": count_error_p90,
        "frpMae": mean(&frp_fold_errors),
        "frpAbsoluteErrorP90": frp_error_p90,
        "folds": fold_evaluations
    });
    let frp_paired_cells = frp_mapping.source_quantiles.len();
    let parameters = json!({
        "countMethod": method,
        "countParameters": count_parameters,
        "countFeatures": if method == "poisson" {
            vec!["intercept", "log(1 + MODIS daily-cell detection count)"]
        } else {
            vec!["VIIRS total / MODIS total"]
        },
        "frpMethod": "empirical-quantile-mapping",
        "frpPairedCells": frp_paired_cells,
        "referenceSensor": "VIIRS",
        "uncertainty": "90th percentile absolute error from year-blocked holdouts"
    });
    let model_id = insert_model(
        &transaction,
        run_id,
        method,
        &parameters.to_string(),
        &evaluation.to_string(),
        chrono::Utc::now().timestamp_micros(),
    )?;
    insert_frp_mapping(&transaction, model_id, &frp_mapping)?;
    insert_calibrated_metrics(
        &transaction,
        model_id,
        &metrics,
        CalibrationOutput {
            count_method: method,
            count_parameters: &count_parameters,
            count_error_p90,
            frp_mapping: &frp_mapping,
            frp_error_p90,
        },
    )?;
    transaction.commit()?;

    println!(
        "{}",
        json!({
            "calibrationModelId": model_id,
            "preprocessingRunId": run_id,
            "countMethod": method,
            "frpMethod": "empirical-quantile-mapping",
            "referenceSensor": "VIIRS",
            "evaluation": evaluation
        })
    );
    Ok(())
}

fn read_daily_metrics(transaction: &Transaction<'_>, run_id: i64) -> Result<Vec<DailyMetric>> {
    let mut statement = transaction.prepare(
        "
        SELECT utc_date, grid_scheme, cell_x, cell_y, instrument,
               detection_count, frp_sum
        FROM daily_cell_metrics
        WHERE preprocessing_run_id = ?1
        ORDER BY utc_date, grid_scheme, cell_x, cell_y, instrument
        ",
    )?;
    let rows = statement.query_map([run_id], |row| {
        Ok(DailyMetric {
            utc_date: row.get(0)?,
            grid_scheme: row.get(1)?,
            cell_x: row.get(2)?,
            cell_y: row.get(3)?,
            instrument: row.get(4)?,
            detection_count: row.get(5)?,
            frp_sum: row.get(6)?,
        })
    })?;
    Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
}

fn pair_metrics(metrics: &[DailyMetric]) -> Result<Vec<CalibrationPair>> {
    let mut by_cell =
        BTreeMap::<(String, String, i64, i64), (Option<DailyMetric>, Option<DailyMetric>)>::new();
    for metric in metrics {
        let key = (
            metric.utc_date.clone(),
            metric.grid_scheme.clone(),
            metric.cell_x,
            metric.cell_y,
        );
        let sensors = by_cell.entry(key).or_default();
        match metric.instrument.as_str() {
            "MODIS" => sensors.0 = Some(metric.clone()),
            "VIIRS" => sensors.1 = Some(metric.clone()),
            other => bail!("Unexpected instrument in daily metrics: {other}"),
        }
    }
    let mut pairs = Vec::new();
    for ((date, _, _, _), (modis, viirs)) in by_cell {
        let (Some(modis), Some(viirs)) = (modis, viirs) else {
            continue;
        };
        let year = date
            .get(..4)
            .context("Invalid UTC date in daily metrics")?
            .parse::<i32>()
            .with_context(|| format!("Invalid UTC date in daily metrics: {date}"))?;
        pairs.push(CalibrationPair {
            year,
            modis_count: modis.detection_count as f64,
            viirs_count: viirs.detection_count as f64,
            modis_frp: modis.frp_sum,
            viirs_frp: viirs.frp_sum,
        });
    }
    pairs.sort_by_key(|pair| pair.year);
    Ok(pairs)
}

fn fit_count(method: &str, pairs: &[CalibrationPair]) -> Result<Vec<f64>> {
    if method == "ratio" {
        let source_total = pairs.iter().map(|pair| pair.modis_count).sum::<f64>();
        if source_total <= 0.0 {
            bail!("Cannot fit ratio calibration because the MODIS training count total is zero.");
        }
        return Ok(vec![
            pairs.iter().map(|pair| pair.viirs_count).sum::<f64>() / source_total,
        ]);
    }
    fit_poisson(pairs)
}

fn fit_poisson(pairs: &[CalibrationPair]) -> Result<Vec<f64>> {
    if pairs.len() < 2 {
        bail!("Poisson calibration requires at least two paired daily-cell observations.");
    }
    let features = pairs
        .iter()
        .map(|pair| (1.0 + pair.modis_count).ln())
        .collect::<Vec<_>>();
    let (minimum, maximum) = features.iter().fold(
        (f64::INFINITY, f64::NEG_INFINITY),
        |(minimum, maximum), value| (minimum.min(*value), maximum.max(*value)),
    );
    if maximum - minimum < 1.0e-10 {
        bail!("Poisson calibration requires variation in paired MODIS counts.");
    }
    let mean_viirs = pairs.iter().map(|pair| pair.viirs_count).sum::<f64>() / pairs.len() as f64;
    let mut intercept = mean_viirs.max(1.0e-8).ln();
    let mut slope = 0.0;
    for _ in 0..100 {
        let (mut s00, mut s01, mut s11, mut t0, mut t1) = (0.0, 0.0, 0.0, 0.0, 0.0);
        for (pair, x) in pairs.iter().zip(&features) {
            let eta = (intercept + slope * x).clamp(-30.0, 30.0);
            let mu = eta.exp();
            let response = eta + (pair.viirs_count - mu) / mu;
            s00 += mu;
            s01 += mu * x;
            s11 += mu * x * x;
            t0 += mu * response;
            t1 += mu * x * response;
        }
        let determinant = s00 * s11 - s01 * s01;
        if !determinant.is_finite() || determinant <= 1.0e-16 {
            bail!("Poisson calibration could not estimate a stable two-parameter model.");
        }
        let mut next_intercept = (t0 * s11 - t1 * s01) / determinant;
        let mut next_slope = (t1 * s00 - t0 * s01) / determinant;
        if !next_intercept.is_finite() || !next_slope.is_finite() {
            bail!("Poisson calibration produced non-finite coefficients.");
        }
        let mut step = (next_intercept - intercept)
            .abs()
            .max((next_slope - slope).abs());
        if step > 5.0 {
            let scale = 5.0 / step;
            next_intercept = intercept + (next_intercept - intercept) * scale;
            next_slope = slope + (next_slope - slope) * scale;
            step = 5.0;
        }
        intercept = next_intercept;
        slope = next_slope;
        if step < 1.0e-8 {
            return Ok(vec![intercept, slope]);
        }
    }
    bail!("Poisson calibration did not converge within 100 IRLS iterations.")
}

fn predict_count(method: &str, source: f64, parameters: &[f64]) -> f64 {
    if method == "ratio" {
        source * parameters[0]
    } else {
        (parameters[0] + parameters[1] * (1.0 + source).ln())
            .clamp(-30.0, 30.0)
            .exp()
    }
}

fn fit_frp(pairs: &[CalibrationPair]) -> Result<FrpMapping> {
    if pairs.len() < 2 {
        bail!("FRP quantile mapping requires at least two paired daily-cell observations.");
    }
    let mut source_quantiles = pairs.iter().map(|pair| pair.modis_frp).collect::<Vec<_>>();
    let mut reference_quantiles = pairs.iter().map(|pair| pair.viirs_frp).collect::<Vec<_>>();
    source_quantiles.sort_by(f64::total_cmp);
    reference_quantiles.sort_by(f64::total_cmp);
    Ok(FrpMapping {
        source_quantiles,
        reference_quantiles,
    })
}

fn map_frp(source_frp: f64, mapping: &FrpMapping) -> f64 {
    if source_frp <= 0.0 {
        return 0.0;
    }
    let lower = mapping
        .source_quantiles
        .partition_point(|value| *value < source_frp);
    let upper = mapping
        .source_quantiles
        .partition_point(|value| *value <= source_frp);
    let denominator = (mapping.source_quantiles.len() - 1).max(1) as f64;
    let rank = if lower == upper {
        (lower as f64 / denominator).clamp(0.0, 1.0)
    } else {
        ((lower + upper - 1) as f64 / (2.0 * denominator)).clamp(0.0, 1.0)
    };
    let position = rank * (mapping.reference_quantiles.len() - 1) as f64;
    let low = position.floor() as usize;
    let high = (low + 1).min(mapping.reference_quantiles.len() - 1);
    let fraction = position - low as f64;
    mapping.reference_quantiles[low] * (1.0 - fraction)
        + mapping.reference_quantiles[high] * fraction
}

fn mean(values: &[f64]) -> f64 {
    values.iter().sum::<f64>() / values.len() as f64
}

fn quantile(values: &[f64], probability: f64) -> Result<f64> {
    if values.is_empty() {
        bail!("Cannot compute an uncertainty quantile without holdout errors.");
    }
    let mut ordered = values.to_vec();
    ordered.sort_by(f64::total_cmp);
    let index = ((probability * ordered.len() as f64).ceil() as usize)
        .saturating_sub(1)
        .min(ordered.len() - 1);
    Ok(ordered[index])
}

fn insert_model(
    transaction: &Transaction<'_>,
    run_id: i64,
    method: &str,
    parameters: &str,
    evaluation: &str,
    created_at_utc_us: i64,
) -> Result<i64> {
    transaction.execute(
        "
        DELETE FROM calibration_models
        WHERE preprocessing_run_id = ?1 AND count_method = ?2
          AND frp_method = 'empirical-quantile-mapping'
        ",
        params![run_id, method],
    )?;
    transaction.execute(
        "
        INSERT INTO calibration_models (
            preprocessing_run_id, count_method, frp_method, reference_sensor,
            algorithm_version, parameters_json, evaluation_json, created_at_utc_us
        ) VALUES (?1, ?2, 'empirical-quantile-mapping', 'VIIRS', ?3, ?4, ?5, ?6)
        ",
        params![
            run_id,
            method,
            ALGORITHM_VERSION,
            parameters,
            evaluation,
            created_at_utc_us
        ],
    )?;
    Ok(transaction.last_insert_rowid())
}

fn insert_frp_mapping(
    transaction: &Transaction<'_>,
    model_id: i64,
    mapping: &FrpMapping,
) -> Result<()> {
    let mut statement = transaction.prepare(
        "
        INSERT INTO calibration_frp_quantiles (
            calibration_model_id, knot_index, modis_frp, viirs_frp
        ) VALUES (?1, ?2, ?3, ?4)
        ",
    )?;
    for (index, (source, reference)) in mapping
        .source_quantiles
        .iter()
        .zip(&mapping.reference_quantiles)
        .enumerate()
    {
        statement.execute(params![model_id, index as i64, source, reference])?;
    }
    Ok(())
}

fn insert_calibrated_metrics(
    transaction: &Transaction<'_>,
    model_id: i64,
    metrics: &[DailyMetric],
    output: CalibrationOutput<'_>,
) -> Result<()> {
    let mut statement = transaction.prepare(
        "
        INSERT INTO calibrated_daily_metrics (
            calibration_model_id, utc_date, grid_scheme, cell_x, cell_y, instrument,
            source_detection_count, calibrated_detection_count, count_lower, count_upper,
            source_frp_sum, calibrated_frp_sum, frp_lower, frp_upper
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
        ",
    )?;
    for metric in metrics {
        let is_modis = metric.instrument == "MODIS";
        let calibrated_count = if is_modis {
            predict_count(
                output.count_method,
                metric.detection_count as f64,
                output.count_parameters,
            )
        } else {
            metric.detection_count as f64
        };
        let calibrated_frp = if is_modis {
            map_frp(metric.frp_sum, output.frp_mapping)
        } else {
            metric.frp_sum
        };
        statement.execute(params![
            model_id,
            metric.utc_date,
            metric.grid_scheme,
            metric.cell_x,
            metric.cell_y,
            metric.instrument,
            metric.detection_count,
            calibrated_count,
            if is_modis {
                (calibrated_count - output.count_error_p90).max(0.0)
            } else {
                calibrated_count
            },
            if is_modis {
                calibrated_count + output.count_error_p90
            } else {
                calibrated_count
            },
            metric.frp_sum,
            calibrated_frp,
            if is_modis {
                (calibrated_frp - output.frp_error_p90).max(0.0)
            } else {
                calibrated_frp
            },
            if is_modis {
                calibrated_frp + output.frp_error_p90
            } else {
                calibrated_frp
            }
        ])?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pair(
        year: i32,
        modis_count: f64,
        viirs_count: f64,
        modis_frp: f64,
        viirs_frp: f64,
    ) -> CalibrationPair {
        CalibrationPair {
            year,
            modis_count,
            viirs_count,
            modis_frp,
            viirs_frp,
        }
    }

    #[test]
    fn ratio_fit_and_poisson_fit_are_finite() {
        let pairs = [
            pair(2020, 1.0, 2.0, 10.0, 12.0),
            pair(2021, 2.0, 4.0, 20.0, 24.0),
            pair(2022, 4.0, 8.0, 40.0, 48.0),
        ];
        let ratio = fit_count("ratio", &pairs).unwrap();
        assert_eq!(ratio, vec![2.0]);
        let poisson = fit_count("poisson", &pairs).unwrap();
        assert!(poisson.iter().all(|value| value.is_finite()));
    }

    #[test]
    fn empirical_mapping_interpolates_and_keeps_zero_at_zero() {
        let mapping = fit_frp(&[
            pair(2020, 1.0, 1.0, 10.0, 20.0),
            pair(2021, 2.0, 2.0, 20.0, 40.0),
            pair(2022, 3.0, 3.0, 30.0, 60.0),
        ])
        .unwrap();
        assert_eq!(map_frp(0.0, &mapping), 0.0);
        assert!(map_frp(15.0, &mapping) > 20.0);
    }
}
