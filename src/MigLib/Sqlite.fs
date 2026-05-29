namespace MigLib

open System
open Microsoft.Data.Sqlite

/// Controls whether MigLib changes SQLite's journal mode when opening
/// transaction connections.
type SqliteJournalMode =
  | Preserve
  | Wal
  | Delete

module internal Sqlite =

  let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

  let ensureInitialized () = sqliteInitialized.Force()

  type ConnectionConfig =
    {
      journalMode: SqliteJournalMode
      busyTimeout: TimeSpan option
    }

  let defaultConnectionConfig =
    {
      journalMode = Preserve
      busyTimeout = None
    }

  let private timeoutSeconds (timeout: TimeSpan) = int (Math.Ceiling timeout.TotalSeconds)

  let connectionString (config: ConnectionConfig) (dbPath: string) =
    let builder = SqliteConnectionStringBuilder()
    builder.DataSource <- dbPath

    match config.busyTimeout with
    | Some timeout -> builder.DefaultTimeout <- timeoutSeconds timeout
    | None -> ()

    builder.ConnectionString

  let private applyJournalMode (connection: SqliteConnection) journalMode =
    match journalMode with
    | Preserve -> ()
    | Wal
    | Delete ->
      use cmd = connection.CreateCommand()

      cmd.CommandText <-
        match journalMode with
        | Wal -> "PRAGMA journal_mode=WAL;"
        | Delete -> "PRAGMA journal_mode=DELETE;"
        | Preserve -> ""

      cmd.ExecuteScalar() |> ignore

  let openConnectionWithConfig (config: ConnectionConfig) (dbPath: string) =
    ensureInitialized ()
    let connection = new SqliteConnection(connectionString config dbPath)
    connection.Open()

    try
      applyJournalMode connection config.journalMode
    with _ ->
      connection.Dispose()
      reraise ()

    connection

  let openConnection (dbPath: string) =
    openConnectionWithConfig defaultConnectionConfig dbPath

  let createCommand (connection: SqliteConnection) (tx: SqliteTransaction) sql = new SqliteCommand(sql, connection, tx)
