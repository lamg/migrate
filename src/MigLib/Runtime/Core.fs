module internal MigLib.Runtime.Core

open System
open System.IO

let resolveDatabasePath (configuredPath: string) : Result<string, string> =
  if String.IsNullOrWhiteSpace configuredPath then
    Error "Configured database path is empty."
  else
    Ok(Path.GetFullPath configuredPath)

let openSqliteConnection connectionConfig (dbPath: string) =
  MigLib.Sqlite.openConnectionWithConfig connectionConfig dbPath
