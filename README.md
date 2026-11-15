# Fire observations: SQLite and C# CLI

The Python package currently loads typed MODIS and VIIRS models into a pickle.
The one-time SQLite importer reuses that established pickle and preserves the
sensor-specific values in a shared `observations` table:

```bash
uv run python -m nasa_space_apps_data_downloader.data_models.sqlite_import
```

By default, it reads `data/data.pickle` and writes
`data/fire-observations.sqlite`. Override either path with `--pickle` or
`--database`. Import is transactional and refuses to append a second import to
a database containing observations. The pickle is loaded as a complete list,
so importing has the same peak-memory requirement as loading that list in
Python.

## C# query CLI

Initialize an empty database or use the schema initialized by the importer:

```bash
dotnet run --project dotnet/FireData.Cli -- init --database data/fire-observations.sqlite
dotnet run --project dotnet/FireData.Cli -- stats --database data/fire-observations.sqlite
```

The CLI emits observation query results as JSON Lines:

```bash
dotnet run --project dotnet/FireData.Cli -- query --database data/fire-observations.sqlite --instrument VIIRS --limit 0
dotnet run --project dotnet/FireData.Cli -- bbox --database data/fire-observations.sqlite --bbox '-123,37,-121,39'
dotnet run --project dotnet/FireData.Cli -- radius --database data/fire-observations.sqlite --lat 38 --lon -122 --radius-m 5000
dotnet run --project dotnet/FireData.Cli -- query --database data/fire-observations.sqlite --from 2024-04-05 --confidence-class high
```

`bbox` order is west, south, east, north; west greater than east denotes a box
crossing the antimeridian. Spatial queries use SQLite R*Tree only to select
conservative candidates, then apply exact coordinate bounds or spherical
Haversine distance. `query` defaults to 1,000 rows; `--limit 0` removes that
limit.

DBSCAN is available per sensor:

```bash
dotnet run --project dotnet/FireData.Cli -- cluster --database data/fire-observations.sqlite --instrument VIIRS --epsilon-m 500 --min-points 4
```

Cluster assignments, including noise points, are recorded with their parameters
in `preprocessing_runs` and `cluster_memberships`. Cross-sensor clustering is
intentionally not implemented: MODIS and VIIRS have different sensitivity
characteristics, and no scientific harmonization method or thresholds have
been specified. DBSCAN loads the selected observations into memory; spatial
indexing reduces neighborhood lookups, but does not make the entire
preprocessing operation streaming.

## C# processing pipeline (stages 02–05)

The processing pipeline adds non-destructive cleaning, per-sensor
spatiotemporal clustering, daily-cell aggregation, and optional exploratory
VIIRS-reference calibration. The current workspace database already contains
1,187,501 observations (324,071 MODIS and 863,430 VIIRS), spanning 2000–2026,
but is schema v1. Back it up, then apply the additive v2 migration before
processing; the migration retains all source observations:

```bash
dotnet run --project dotnet/FireData.Cli -- migrate --database data/fire-observations.sqlite
dotnet run --project dotnet/FireData.Cli -- stats --database data/fire-observations.sqlite
```

To run a small one-month smoke test first:

```bash
dotnet run --project dotnet/FireData.Cli -- process --database data/fire-observations.sqlite --from 2025-01-01 --to 2025-02-01
```

The command reports a `preprocessingRunId`. When ready, process the full dataset
(no date or instrument filters):

```bash
dotnet run --project dotnet/FireData.Cli -- process --database data/fire-observations.sqlite
```

The full run writes a separate versioned product and prints its ID. The smoke
test remains stored as another run; processing does not overwrite previous
runs, and reprocessing increases database size. Defaults are a
0.25-degree latitude/longitude grid, a 2 km spatial epsilon, a 1.5 km/day time
scale, and DBSCAN `min-points=4`. The spatiotemporal neighborhood metric is
`sqrt(haversine_distance_m^2 + (elapsed_days * time_scale_m_per_day)^2)`, bounded
by `epsilon-m`. MODIS and VIIRS are clustered independently. Parameters and
input filters are recorded on the run; `processing_run_sources` captures the
source-file hashes and selected/processed record counts.

Optional controls include `--grid-degrees`, `--epsilon-m`,
`--time-scale-m-per-day`, `--min-points`, `--instrument MODIS|VIIRS|all`,
`--confidence-class low|nominal|high`, and UTC `--from` / `--to` bounds.
Cleaning excludes selected observations with non-positive/non-finite Scan or
Track, non-finite brightness/FRP, negative FRP, or missing unified confidence;
excluded raw rows are not deleted or changed. Selected and excluded counts are
reported. The processing run is committed atomically. Clustering uses SQLite
temporary tables and the observation R*Tree, and streams neighborhood
candidates rather than retaining the entire selected point set in C# memory.
Temporary SQLite storage needs enough disk space for the staged input and
working queue.

`daily_cell_metrics` contains observed-cell counts, cluster counts, FRP sum/max,
and spherical cell area. `daily_cell_presence_heuristic` separately records
cells with a direct or same-day adjacent-cell detection, including the number
of same-or-adjacent detection cells. It is a regional detection-presence heuristic,
not swath coverage or evidence of observation; it never creates zero-count
activity rows in `daily_cell_metrics`. The grid is a simple lat/lon grid, not
equal-area.

Calibrate a processing run with either a ratio or Poisson count strategy; both
also apply empirical FRP quantile mapping:

```bash
dotnet run --project dotnet/FireData.Cli -- calibrate --database data/fire-observations.sqlite --run-id 1 --count-method ratio
dotnet run --project dotnet/FireData.Cli -- calibrate --database data/fire-observations.sqlite --run-id 1 --count-method poisson
```

Calibration pairs same-date, same-cell MODIS and VIIRS metrics. `poisson` fits
a log-link count model by iteratively reweighted least squares using
`log(1 + MODIS count)`; the strategy registry is the extension point for future
models. The command performs leave-one-UTC-year-out evaluation and refuses to
publish results if paired data or a valid training fold is insufficient. Model
coefficients, method/version, holdout metrics, and 90th-percentile absolute
holdout-error bounds are stored in `calibration_models`; source and calibrated
values remain distinct in `calibrated_daily_metrics`. These empirical bounds
are not formal probabilistic confidence intervals; the learned FRP mapping
knots are retained in `calibration_frp_quantiles`. All cross-sensor calibration
remains exploratory with VIIRS as a provisional reference.

Calibration is a separate, optional command and must use the run ID from the
full-dataset run (not the smoke-test run). Use one of:

```bash
dotnet run --project dotnet/FireData.Cli -- calibrate --database data/fire-observations.sqlite --run-id <FULL_RUN_ID> --count-method ratio
dotnet run --project dotnet/FireData.Cli -- calibrate --database data/fire-observations.sqlite --run-id <FULL_RUN_ID> --count-method poisson
```

Calibration only uses same-date, same-cell pairs with both MODIS and VIIRS
detections. It requires at least two paired cells spanning at least two UTC
years and a valid training fold for each held-out year; the Poisson method also
requires count variation. It may correctly refuse to produce a model if those
conditions are not met. If the database is absent instead of already imported,
first create it using the one-time importer documented above; the importer
expects `data/data.pickle` by default.

## Current preprocessing limitations and remaining work

- The planned Tweedie count calibration is **not implemented**. `ratio` and a
  Poisson log-link model are available. Poisson calibration has no explicit
  observation-effort/swath-coverage offset, so it remains exploratory.
- Cleaning uses fixed validity checks (finite brightness/FRP/Scan/Track,
  positive Scan/Track, non-negative FRP, and a unified confidence value).
  There is no user-configurable cleaning policy or brightness/FRP/hotspot-type
  filter in `process`; its selectable inputs are instrument, confidence class,
  and UTC time bounds.
- The strategy extension point currently covers count calibration and FRP
  mapping only. A classification-strategy registry has not been added.
- The processing CLI writes products for later use, but API routes for events,
  daily cells, calibrated metrics, baselines, or anomalies are outside this
  implementation.
- Regression tests cover additive migration and a synthetic end-to-end
  processing/calibration fixture, not all clustering boundaries, spatial
  edge-cases, or full-dataset performance. The full 1.18-million-row dataset
  has not been processed or benchmarked with this implementation. Clustering
  bounds C# memory by staging in SQLite, but still performs repeated R*Tree
  neighborhood searches; expect a potentially long run and ensure adequate
  free disk for temporary staging plus durable outputs before starting.
- Calibration stores blocked-year MAE/RMSE and empirical 90th-percentile
  absolute-error bounds, not formal confidence/prediction intervals or a
  comprehensive model diagnostic report. The FRP mapping is learned from
  daily-cell FRP sums.

Run the processing and migration regression tests with:

```bash
dotnet test dotnet/dotnet.slnx
```

The database stores timestamps as UTC epoch microseconds and retains original
MODIS confidence percentages and VIIRS confidence enums. `confidence_class` is
a stored generated column for common cross-sensor filtering, not a replacement
for those source values.

## ASP.NET Core API

Run the API from the repository root after importing the database. Its
development launch profile listens on `http://localhost:5080`:

```bash
dotnet run --project dotnet/FireData.Api --urls http://localhost:5080
```

By default, the API connects to `data/fire-observations.sqlite`. Override this
with the `ConnectionStrings__FireData` environment variable, for example:

```bash
ConnectionStrings__FireData='Data Source=/absolute/path/fire-observations.sqlite;Foreign Keys=True' \
  dotnet run --project dotnet/FireData.Api --urls http://localhost:5080
```

`GET /api/observations` supports the following query parameters:

- `dateFrom` / `dateTo`: inclusive UTC calendar dates (`YYYY-MM-DD`).
- `acquiredFrom` / `acquiredTo`: ISO 8601 acquisition timestamps with an
  explicit UTC offset (for example, `2024-04-05T12:00:00Z`); the upper bound is
  exclusive.
- `timeOfDayFrom` / `timeOfDayTo`: UTC `HH:mm[:ss]` bounds; the upper bound is
  exclusive, and both are required to express a range crossing midnight.
- AOI: either `west`, `south`, `east`, `north` for a bounding box (including
  antimeridian-crossing boxes), or `centerLatitude`, `centerLongitude`,
  `radiusMeters` for a circle. Radius searches use R*Tree candidates followed
  by exact spherical distance filtering.
- `confidence`: `low`, `nominal`, or `high`; `satellite`: `AQUA`, `TERRA`,
  `N`, `N20`, or `N21`.
- `brightnessMin` / `brightnessMax` in Kelvin and `frpMin` / `frpMax` in MW.
- `type`: `VEGETATION_FIRE`, `ACTIVE_VOLCANO`,
  `OTHER_STATIC_LAND_SOURCE`, or `OFFSHORE` (MODIS only; VIIRS rows do not
  match).
- `pageSize`: 1–500 (default 100). Results are ordered by acquisition time
  then observation ID. Pass both `afterAcquiredAt` and
  `afterObservationId` from the returned `next` cursor to continue paging.

Invalid combinations or out-of-range values return validation problem details.
The development environment exposes the OpenAPI document at `/openapi/v1.json`.
Example requests are in [FireData.Api.http](./dotnet/FireData.Api/FireData.Api.http).
