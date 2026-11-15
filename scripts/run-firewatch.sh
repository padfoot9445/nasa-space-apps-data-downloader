#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
COMPOSE_FILE="$ROOT_DIR/compose.yaml"
DATABASE_PATH="${FIREDATA_DB_PATH:-$ROOT_DIR/data/fire-observations.sqlite}"
OPEN_BROWSER=1
DATABASE_PROVIDED=0

usage() {
    cat <<'EOF'
Usage: scripts/run-firewatch.sh [database-path] [--no-browser]

Builds and starts Firewatch with Docker Compose, waits for the UI, then opens it.
The database must already exist and is mounted read-only into the container.

Environment:
  FIREDATA_DB_PATH  Database file (default: data/fire-observations.sqlite)
  FIREDATA_PORT     Local web port (default: 5080)
  DOTNET_VERSION    .NET container tag (default: 11.0)

To stop the server:
  docker compose -f compose.yaml down
EOF
}

for argument in "$@"; do
    case "$argument" in
        --no-browser)
            OPEN_BROWSER=0
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        -*)
            printf 'Unknown option: %s\n\n' "$argument" >&2
            usage >&2
            exit 2
            ;;
        *)
            if [[ "$DATABASE_PROVIDED" -eq 1 ]]; then
                printf 'Only one database path may be provided.\n' >&2
                exit 2
            fi
            DATABASE_PATH="$argument"
            DATABASE_PROVIDED=1
            ;;
    esac
done

if [[ ! -f "$DATABASE_PATH" ]]; then
    printf 'Database file not found: %s\n' "$DATABASE_PATH" >&2
    printf 'Pass an existing SQLite file, for example:\n  %s /path/to/fire-observations.sqlite\n' "$0" >&2
    exit 1
fi

DATABASE_DIR="$(cd -- "$(dirname -- "$DATABASE_PATH")" && pwd -P)"
export FIREDATA_DB_PATH="$DATABASE_DIR/$(basename -- "$DATABASE_PATH")"
export FIREDATA_PORT="${FIREDATA_PORT:-5080}"
if ! [[ "$FIREDATA_PORT" =~ ^[0-9]{1,5}$ ]] ||
    (( FIREDATA_PORT < 1 || FIREDATA_PORT > 65535 )); then
    printf 'FIREDATA_PORT must be an integer between 1 and 65535.\n' >&2
    exit 2
fi
URL="http://127.0.0.1:${FIREDATA_PORT}"

if ! command -v docker >/dev/null 2>&1; then
    printf 'Docker is required. Install Docker Desktop or Docker Engine with Compose v2.\n' >&2
    exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
    printf 'Docker Compose v2 is required (try: docker compose version).\n' >&2
    exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
    printf 'curl is required to check that the web app has started.\n' >&2
    exit 1
fi

printf 'Building and starting Firewatch...\n'
docker compose --project-directory "$ROOT_DIR" -f "$COMPOSE_FILE" up --build --detach

printf 'Waiting for %s ' "$URL"
ready=0
for _ in $(seq 1 60); do
    if curl --fail --silent "$URL/" >/dev/null; then
        ready=1
        break
    fi
    printf '.'
    sleep 1
done
printf '\n'

if [[ "$ready" -ne 1 ]]; then
    printf 'Firewatch did not become ready. Recent container logs:\n' >&2
    docker compose --project-directory "$ROOT_DIR" -f "$COMPOSE_FILE" logs --tail 80 >&2
    exit 1
fi

printf 'Firewatch is running at %s\n' "$URL"
if [[ "$OPEN_BROWSER" -eq 1 ]]; then
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$URL" >/dev/null 2>&1 &
    elif command -v open >/dev/null 2>&1; then
        open "$URL" >/dev/null 2>&1 &
    else
        printf 'No browser launcher found. Open %s in your browser.\n' "$URL"
    fi
fi
