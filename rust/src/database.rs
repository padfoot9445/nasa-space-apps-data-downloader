use std::path::Path;

use anyhow::{Context, Result, bail};
use rusqlite::Connection;

pub fn open_existing(path: &Path) -> Result<Connection> {
    if !path.is_file() {
        bail!(
            "Database not found: {}. Import data with the Python sqlite_import module first.",
            path.display()
        );
    }

    let connection = Connection::open(path)
        .with_context(|| format!("Opening SQLite database {}", path.display()))?;
    connection.busy_timeout(std::time::Duration::from_secs(30))?;
    connection.pragma_update(None, "foreign_keys", "ON")?;

    let version: i64 = connection.query_row("PRAGMA user_version", [], |row| row.get(0))?;
    if version != 2 {
        bail!(
            "Unsupported or missing database schema version {version}; expected 2. Run the existing C# CLI migrate command."
        );
    }

    Ok(connection)
}
