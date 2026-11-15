mod calibration;
mod database;
mod processing;
mod spatial;

use std::path::PathBuf;

use anyhow::Result;
use clap::{Parser, Subcommand};

#[derive(Debug, Parser)]
#[command(
    name = "fire-data-rust",
    version,
    about = "Rust fire-data preprocessing CLI"
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    Process {
        #[arg(long, default_value = "data/fire-observations.sqlite")]
        database: PathBuf,
        #[arg(long, default_value_t = 0.25)]
        grid_degrees: f64,
        #[arg(long, default_value_t = 2_000.0)]
        epsilon_m: f64,
        #[arg(long, default_value_t = 1_500.0)]
        time_scale_m_per_day: f64,
        #[arg(long, default_value_t = 4)]
        min_points: i32,
        #[arg(long, default_value = "all")]
        instrument: String,
        #[arg(long)]
        confidence_class: Option<String>,
        #[arg(long)]
        from: Option<String>,
        #[arg(long)]
        to: Option<String>,
    },
    Calibrate {
        #[arg(long, default_value = "data/fire-observations.sqlite")]
        database: PathBuf,
        #[arg(long)]
        run_id: i64,
        #[arg(long, default_value = "ratio")]
        count_method: String,
    },
}

fn main() {
    if let Err(error) = run() {
        eprintln!("Error: {error:#}");
        std::process::exit(2);
    }
}

fn run() -> Result<()> {
    match Cli::parse().command {
        Command::Process {
            database,
            grid_degrees,
            epsilon_m,
            time_scale_m_per_day,
            min_points,
            instrument,
            confidence_class,
            from,
            to,
        } => processing::run(
            &database,
            processing::ProcessOptions {
                grid_degrees,
                epsilon_m,
                time_scale_m_per_day,
                min_points,
                instrument,
                confidence_class,
                from,
                to,
            },
        ),
        Command::Calibrate {
            database,
            run_id,
            count_method,
        } => calibration::run(&database, run_id, &count_method),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cli_has_process_and_calibrate_commands() {
        use clap::error::ErrorKind;

        assert_eq!(
            Cli::try_parse_from(["fire-data-rust", "process", "--help"])
                .unwrap_err()
                .kind(),
            ErrorKind::DisplayHelp
        );
        assert_eq!(
            Cli::try_parse_from(["fire-data-rust", "calibrate", "--help"])
                .unwrap_err()
                .kind(),
            ErrorKind::DisplayHelp
        );
    }
}
