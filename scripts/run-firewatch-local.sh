#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
DATABASE_PATH="${FIREDATA_DB_PATH:-$ROOT_DIR/data/fire-observations.sqlite}"
OPEN_BROWSER=1
DATABASE_PROVIDED=0

usage() {
    cat <<'EOF'
Usage: scripts/run-firewatch-local.sh [database-path] [--no-browser]

Runs Firewatch directly with the .NET SDK; Docker is not required.
The database must already exist and is opened read-only.

Environment:
  FIREDATA_DB_PATH  Database file (default: data/fire-observations.sqlite)
  FIREDATA_PORT     Local web port (default: 5080)

Stop the server with Ctrl+C.
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

if ! command -v dotnet >/dev/null 2>&1; then
    printf '.NET SDK 11 is required. Install it, then run this script again.\n' >&2
    exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
    printf 'curl is required to check that the web app has started.\n' >&2
    exit 1
fi

DATABASE_DIR="$(cd -- "$(dirname -- "$DATABASE_PATH")" && pwd -P)"
export ConnectionStrings__FireData="Data Source=$DATABASE_DIR/$(basename -- "$DATABASE_PATH");Mode=ReadOnly;Foreign Keys=True"
export FIREDATA_PORT="${FIREDATA_PORT:-5080}"
if ! [[ "$FIREDATA_PORT" =~ ^[0-9]{1,5}$ ]] ||
    (( FIREDATA_PORT < 1 || FIREDATA_PORT > 65535 )); then
    printf 'FIREDATA_PORT must be an integer between 1 and 65535.\n' >&2
    exit 2
fi
export ASPNETCORE_URLS="http://127.0.0.1:$FIREDATA_PORT"
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ENVIRONMENT=Development
URL="$ASPNETCORE_URLS"

cd "$ROOT_DIR/dotnet/FireData.Api"
printf 'Starting Firewatch with the read-only database at %s\n' "$URL"
dotnet run --no-launch-profile &
APP_PID=$!
trap 'kill "$APP_PID" 2>/dev/null || true' EXIT

printf 'Waiting for the web app '
ready=0
for _ in {1..60}; do
    if curl --fail --silent "$URL/" >/dev/null; then
        ready=1
        break
    fi
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        break
    fi
    printf '.'
    sleep 1
done
printf '\n'

if [[ "$ready" -ne 1 ]]; then
    printf 'Firewatch did not become ready; check the .NET output above.\n' >&2
    exit 1
fi

printf 'Firewatch is running at %s. Press Ctrl+C to stop.\n' "$URL"
if [[ "$OPEN_BROWSER" -eq 1 ]]; then
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$URL" >/dev/null 2>&1 &
    elif command -v open >/dev/null 2>&1; then
        open "$URL" >/dev/null 2>&1 &
    else
        printf 'No browser launcher found. Open %s in your browser.\n' "$URL"
    fi
fi

wait "$APP_PID"
