module internal MigLib.Sqlite

open Microsoft.Data.Sqlite

let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

let ensureInitialized () = sqliteInitialized.Force()

let connectionString (dbPath: string) = $"Data Source={dbPath}"

let openConnection (dbPath: string) =
  ensureInitialized ()
  let connection = new SqliteConnection(connectionString dbPath)
  connection.Open()
  connection

let createCommand (connection: SqliteConnection) (tx: SqliteTransaction) sql = new SqliteCommand(sql, connection, tx)
