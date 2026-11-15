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
