use std::f64::consts::PI;

use anyhow::{Result, bail};
use serde::Serialize;

const EARTH_RADIUS_METERS: f64 = 6_371_008.8;

#[derive(Clone, Copy, Debug, PartialEq, Serialize)]
pub struct LongitudeInterval {
    pub west: f64,
    pub east: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct SpatialEnvelope {
    pub south: f64,
    pub north: f64,
    pub longitude_intervals: Vec<LongitudeInterval>,
}

pub fn to_unix_microseconds(value: &str) -> Result<i64> {
    if let Ok(date) = chrono::NaiveDate::parse_from_str(value, "%Y-%m-%d") {
        return Ok(date
            .and_hms_opt(0, 0, 0)
            .expect("midnight is valid")
            .and_utc()
            .timestamp_micros());
    }

    if let Ok(timestamp) = chrono::NaiveDateTime::parse_from_str(value, "%Y-%m-%dT%H:%M:%S%.f") {
        return Ok(timestamp.and_utc().timestamp_micros());
    }
    if let Ok(timestamp) = chrono::NaiveDateTime::parse_from_str(value, "%Y-%m-%d %H:%M:%S%.f") {
        return Ok(timestamp.and_utc().timestamp_micros());
    }

    let timestamp = chrono::DateTime::parse_from_rfc3339(value).map_err(|_| {
        anyhow::anyhow!("timestamp must be an ISO-8601 timestamp or date interpreted as UTC")
    })?;
    Ok(timestamp.timestamp_micros())
}

pub fn distance_meters(latitude1: f64, longitude1: f64, latitude2: f64, longitude2: f64) -> f64 {
    let lat1 = latitude1.to_radians();
    let lat2 = latitude2.to_radians();
    let delta_lat = lat2 - lat1;
    let delta_lon = (longitude2 - longitude1).to_radians();
    let sin_lat = (delta_lat / 2.0).sin();
    let sin_lon = (delta_lon / 2.0).sin();
    let a = sin_lat.powi(2) + lat1.cos() * lat2.cos() * sin_lon.powi(2);
    2.0 * EARTH_RADIUS_METERS * a.clamp(0.0, 1.0).sqrt().asin()
}

pub fn envelope_for_radius(
    latitude: f64,
    longitude: f64,
    radius_meters: f64,
) -> Result<SpatialEnvelope> {
    if !(-90.0..=90.0).contains(&latitude) {
        bail!("Latitude must be within [-90, 90].");
    }
    if !(-180.0..=180.0).contains(&longitude) {
        bail!("Longitude must be within [-180, 180].");
    }
    if !radius_meters.is_finite() || radius_meters < 0.0 {
        bail!("Radius must be non-negative and finite.");
    }

    let angular_radius = radius_meters / EARTH_RADIUS_METERS;
    let latitude_radians = latitude.to_radians();
    let latitude_delta = angular_radius.to_degrees();
    let south = (-90.0_f64).max(latitude - latitude_delta);
    let north = 90.0_f64.min(latitude + latitude_delta);
    let reaches_pole = angular_radius >= PI / 2.0 - latitude_radians.abs();
    if reaches_pole {
        return Ok(SpatialEnvelope {
            south,
            north,
            longitude_intervals: vec![LongitudeInterval {
                west: -180.0,
                east: 180.0,
            }],
        });
    }

    let longitude_delta = (angular_radius.sin() / latitude_radians.cos())
        .asin()
        .to_degrees();
    if longitude_delta >= 180.0 {
        return Ok(SpatialEnvelope {
            south,
            north,
            longitude_intervals: vec![LongitudeInterval {
                west: -180.0,
                east: 180.0,
            }],
        });
    }

    let west = longitude - longitude_delta;
    let east = longitude + longitude_delta;
    let longitude_intervals = if west < -180.0 {
        vec![
            LongitudeInterval { west: -180.0, east },
            LongitudeInterval {
                west: west + 360.0,
                east: 180.0,
            },
        ]
    } else if east > 180.0 {
        vec![
            LongitudeInterval {
                west: -180.0,
                east: east - 360.0,
            },
            LongitudeInterval { west, east: 180.0 },
        ]
    } else {
        vec![LongitudeInterval { west, east }]
    };

    Ok(SpatialEnvelope {
        south,
        north,
        longitude_intervals,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn distance_matches_known_equatorial_degree() {
        let distance = distance_meters(0.0, 0.0, 0.0, 1.0);
        assert!((distance - 111_195.08).abs() < 1.0);
    }

    #[test]
    fn envelope_splits_at_antimeridian() {
        let envelope = envelope_for_radius(0.0, 179.99, 2_000.0).unwrap();
        assert_eq!(envelope.longitude_intervals.len(), 2);
        assert_eq!(envelope.longitude_intervals[0].west, -180.0);
        assert_eq!(envelope.longitude_intervals[1].east, 180.0);
    }

    #[test]
    fn parses_date_and_offsets_as_utc_microseconds() {
        let date = to_unix_microseconds("2024-01-01").unwrap();
        let offset = to_unix_microseconds("2024-01-01T01:00:00+01:00").unwrap();
        let utc = to_unix_microseconds("2024-01-01T00:00:00").unwrap();
        assert_eq!(date, offset);
        assert_eq!(date, utc);
    }
}
