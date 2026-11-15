# Diff Details

Date : 2026-11-15 20:50:36

Directory /home/padfoot9445/Coding/nasa-space-apps-data-downloader

Total : 44 files,  7831 codes, 75 comments, 779 blanks, all 8685 lines

[Summary](results.md) / [Details](details.md) / [Diff Summary](diff.md) / Diff Details

## Files
| filename | language | code | comment | blank | total |
| :--- | :--- | ---: | ---: | ---: | ---: |
| [.dockerignore](/.dockerignore) | Ignore | 15 | 0 | 1 | 16 |
| [Dockerfile](/Dockerfile) | Docker | 21 | 0 | 6 | 27 |
| [README.md](/README.md) | Markdown | 286 | 0 | 53 | 339 |
| [compose.yaml](/compose.yaml) | YAML | 22 | 0 | 1 | 23 |
| [dotnet/FireData.Api/Components/App.razor](/dotnet/FireData.Api/Components/App.razor) | ASP.NET Razor | 18 | 0 | 1 | 19 |
| [dotnet/FireData.Api/Components/Pages/Home.razor](/dotnet/FireData.Api/Components/Pages/Home.razor) | ASP.NET Razor | 661 | 0 | 52 | 713 |
| [dotnet/FireData.Api/Components/Routes.razor](/dotnet/FireData.Api/Components/Routes.razor) | ASP.NET Razor | 12 | 0 | 1 | 13 |
| [dotnet/FireData.Api/Components/\_Imports.razor](/dotnet/FireData.Api/Components/_Imports.razor) | ASP.NET Razor | 11 | 0 | 1 | 12 |
| [dotnet/FireData.Api/Endpoints/ObservationEndpoints.cs](/dotnet/FireData.Api/Endpoints/ObservationEndpoints.cs) | C# | 60 | 4 | 9 | 73 |
| [dotnet/FireData.Api/FireData.Api.csproj](/dotnet/FireData.Api/FireData.Api.csproj) | MSBuild | 12 | 0 | 2 | 14 |
| [dotnet/FireData.Api/FireData.Api.http](/dotnet/FireData.Api/FireData.Api.http) | HTTP | 9 | 8 | 9 | 26 |
| [dotnet/FireData.Api/Models/ObservationSearchRequest.cs](/dotnet/FireData.Api/Models/ObservationSearchRequest.cs) | C# | 70 | 43 | 28 | 141 |
| [dotnet/FireData.Api/Models/ObservationTimeSeriesPoint.cs](/dotnet/FireData.Api/Models/ObservationTimeSeriesPoint.cs) | C# | 5 | 4 | 2 | 11 |
| [dotnet/FireData.Api/Program.cs](/dotnet/FireData.Api/Program.cs) | C# | 30 | 0 | 10 | 40 |
| [dotnet/FireData.Api/Properties/launchSettings.json](/dotnet/FireData.Api/Properties/launchSettings.json) | JSON | 13 | 0 | 1 | 14 |
| [dotnet/FireData.Api/Services/IObservationSearchService.cs](/dotnet/FireData.Api/Services/IObservationSearchService.cs) | C# | 8 | 5 | 3 | 16 |
| [dotnet/FireData.Api/Services/IObservationTimeSeriesService.cs](/dotnet/FireData.Api/Services/IObservationTimeSeriesService.cs) | C# | 8 | 2 | 3 | 13 |
| [dotnet/FireData.Api/Services/ObservationSearchRequestValidator.cs](/dotnet/FireData.Api/Services/ObservationSearchRequestValidator.cs) | C# | 225 | 0 | 37 | 262 |
| [dotnet/FireData.Api/Services/ObservationSearchService.cs](/dotnet/FireData.Api/Services/ObservationSearchService.cs) | C# | 260 | 3 | 34 | 297 |
| [dotnet/FireData.Api/Services/ObservationTimeSeriesService.cs](/dotnet/FireData.Api/Services/ObservationTimeSeriesService.cs) | C# | 87 | 2 | 11 | 100 |
| [dotnet/FireData.Api/wwwroot/css/fire-map.css](/dotnet/FireData.Api/wwwroot/css/fire-map.css) | PostCSS | 159 | 0 | 7 | 166 |
| [dotnet/FireData.Api/wwwroot/js/fire-map.js](/dotnet/FireData.Api/wwwroot/js/fire-map.js) | JavaScript | 369 | 0 | 42 | 411 |
| [dotnet/FireData.Cli.Tests/FireData.Cli.Tests.csproj](/dotnet/FireData.Cli.Tests/FireData.Cli.Tests.csproj) | MSBuild | 22 | 0 | 3 | 25 |
| [dotnet/FireData.Cli.Tests/ProcessingPipelineTests.cs](/dotnet/FireData.Cli.Tests/ProcessingPipelineTests.cs) | C# | 404 | 0 | 26 | 430 |
| [dotnet/FireData.Cli/AssemblyInfo.cs](/dotnet/FireData.Cli/AssemblyInfo.cs) | C# | 2 | 0 | 2 | 4 |
| [dotnet/FireData.Cli/CalibrationCommands.cs](/dotnet/FireData.Cli/CalibrationCommands.cs) | C# | 415 | 0 | 33 | 448 |
| [dotnet/FireData.Cli/CalibrationStrategies.cs](/dotnet/FireData.Cli/CalibrationStrategies.cs) | C# | 205 | 0 | 32 | 237 |
| [dotnet/FireData.Cli/Commands.cs](/dotnet/FireData.Cli/Commands.cs) | C# | 58 | 0 | 7 | 65 |
| [dotnet/FireData.Cli/DatabaseDbscan.cs](/dotnet/FireData.Cli/DatabaseDbscan.cs) | C# | 454 | 0 | 33 | 487 |
| [dotnet/FireData.Cli/FireData.Cli.csproj](/dotnet/FireData.Cli/FireData.Cli.csproj) | MSBuild | 1 | 0 | 0 | 1 |
| [dotnet/FireData.Cli/ProcessingCommands.cs](/dotnet/FireData.Cli/ProcessingCommands.cs) | C# | 626 | 0 | 50 | 676 |
| [rust/Cargo.lock](/rust/Cargo.lock) | TOML | 692 | 2 | 91 | 785 |
| [rust/Cargo.toml](/rust/Cargo.toml) | TOML | 16 | 0 | 3 | 19 |
| [rust/src/calibration.rs](/rust/src/calibration.rs) | Rust | 533 | 0 | 30 | 563 |
| [rust/src/database.rs](/rust/src/database.rs) | Rust | 22 | 0 | 6 | 28 |
| [rust/src/main.rs](/rust/src/main.rs) | Rust | 106 | 0 | 10 | 116 |
| [rust/src/processing.rs](/rust/src/processing.rs) | Rust | 1,302 | 0 | 63 | 1,365 |
| [rust/src/spatial.rs](/rust/src/spatial.rs) | Rust | 138 | 0 | 18 | 156 |
| [scripts/run-firewatch-local.sh](/scripts/run-firewatch-local.sh) | Shell Script | 99 | 1 | 15 | 115 |
| [scripts/run-firewatch.ps1](/scripts/run-firewatch.ps1) | PowerShell | 63 | 0 | 10 | 73 |
| [scripts/run-firewatch.sh](/scripts/run-firewatch.sh) | Shell Script | 97 | 1 | 14 | 112 |
| [sqlite/migrations/002\_processing.sql](/sqlite/migrations/002_processing.sql) | SQLite | 105 | 0 | 9 | 114 |
| [sqlite/schema.sql](/sqlite/schema.sql) | SQLite | 104 | 0 | 10 | 114 |
| [src/nasa\_space\_apps\_data\_downloader/data\_models/test\_sqlite\_import.py](/src/nasa_space_apps_data_downloader/data_models/test_sqlite_import.py) | Python | 6 | 0 | 0 | 6 |

[Summary](results.md) / [Details](details.md) / [Diff Summary](diff.md) / Diff Details