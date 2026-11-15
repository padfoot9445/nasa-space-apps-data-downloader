param(
    [string]$DatabasePath = "",
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$composeFile = Join-Path $rootDir "compose.yaml"

if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    if ([string]::IsNullOrWhiteSpace($env:FIREDATA_DB_PATH)) {
        $DatabasePath = Join-Path $rootDir "data/fire-observations.sqlite"
    }
    else {
        $DatabasePath = $env:FIREDATA_DB_PATH
    }
}

$resolvedDatabase = (Resolve-Path -LiteralPath $DatabasePath).Path
if ((Get-Item -LiteralPath $resolvedDatabase) -is [System.IO.DirectoryInfo]) {
    throw "DatabasePath must name a SQLite file: $resolvedDatabase"
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    throw "Docker is required. Install Docker Desktop with Docker Compose v2."
}
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose v2 is required (try: docker compose version)."
}

$env:FIREDATA_DB_PATH = $resolvedDatabase
if ([string]::IsNullOrWhiteSpace($env:FIREDATA_PORT)) {
    $env:FIREDATA_PORT = "5080"
}
if ($env:FIREDATA_PORT -notmatch '^\d{1,5}$' -or
    [int]$env:FIREDATA_PORT -lt 1 -or [int]$env:FIREDATA_PORT -gt 65535) {
    throw "FIREDATA_PORT must be an integer between 1 and 65535."
}
$url = "http://127.0.0.1:$($env:FIREDATA_PORT)"

Write-Host "Building and starting Firewatch..."
docker compose --project-directory $rootDir -f $composeFile up --build --detach
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose failed to start Firewatch."
}

$ready = $false
Write-Host "Waiting for $url "
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        Invoke-WebRequest -Uri "$url/" -TimeoutSec 2 | Out-Null
        $ready = $true
        break
    }
    catch {
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 1
    }
}
Write-Host ""

if (-not $ready) {
    docker compose --project-directory $rootDir -f $composeFile logs --tail 80
    throw "Firewatch did not become ready. Check the container logs above."
}

Write-Host "Firewatch is running at $url"
if (-not $NoBrowser) {
    Start-Process $url
}
