module MigLib.Db

open System.Threading.Tasks
open Microsoft.Data.Sqlite

[<Literal>]
let Rfc3339UtcNow = "strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')"

type AutoIncPKAttribute = DbAttributes.AutoIncPKAttribute
type PKAttribute = DbAttributes.PKAttribute
type UniqueAttribute = DbAttributes.UniqueAttribute
type DefaultAttribute = DbAttributes.DefaultAttribute
type DefaultExprAttribute = DbAttributes.DefaultExprAttribute
type IndexAttribute = DbAttributes.IndexAttribute
type SelectAllAttribute = DbAttributes.SelectAllAttribute
type SelectByAttribute = DbAttributes.SelectByAttribute
type SelectOneByAttribute = DbAttributes.SelectOneByAttribute
type SelectLikeAttribute = DbAttributes.SelectLikeAttribute
type SelectByOrInsertAttribute = DbAttributes.SelectByOrInsertAttribute
type UpdateByAttribute = DbAttributes.UpdateByAttribute
type DeleteByAttribute = DbAttributes.DeleteByAttribute
type InsertOrIgnoreAttribute = DbAttributes.InsertOrIgnoreAttribute
type UpsertAttribute = DbAttributes.UpsertAttribute
type OnDeleteCascadeAttribute = DbAttributes.OnDeleteCascadeAttribute
type OnDeleteSetNullAttribute = DbAttributes.OnDeleteSetNullAttribute
type ViewAttribute = DbAttributes.ViewAttribute
type JoinAttribute = DbAttributes.JoinAttribute
type ViewSqlAttribute = DbAttributes.ViewSqlAttribute
type OrderByAttribute = DbAttributes.OrderByAttribute
type PreviousNameAttribute = DbAttributes.PreviousNameAttribute
type DropColumnAttribute = DbAttributes.DropColumnAttribute

let openSqliteConnection = DbCore.openSqliteConnection
let resolveDatabaseFilePath = DbCore.resolveDatabaseFilePath

let querySingle
  (sql: string)
  (configure: SqliteCommand -> unit)
  (readRow: SqliteDataReader -> 'a)
  (tx: SqliteTransaction)
  : Task<Result<'a option, SqliteException>> =
  task {
    try
      use cmd = new SqliteCommand(sql, tx.Connection, tx)
      configure cmd
      use! reader = cmd.ExecuteReaderAsync()
      let! hasRow = reader.ReadAsync()

      if hasRow then
        return Ok(Some(readRow reader))
      else
        return Ok None
    with :? SqliteException as ex ->
      return Error ex
  }

let queryList
  (sql: string)
  (configure: SqliteCommand -> unit)
  (readRow: SqliteDataReader -> 'a)
  (tx: SqliteTransaction)
  : Task<Result<'a list, SqliteException>> =
  task {
    try
      use cmd = new SqliteCommand(sql, tx.Connection, tx)
      configure cmd
      use! reader = cmd.ExecuteReaderAsync()
      let results = ResizeArray<'a>()
      let mutable hasMore = true

      while hasMore do
        let! next = reader.ReadAsync()
        hasMore <- next

        if hasMore then
          results.Add(readRow reader)

      return Ok(results |> Seq.toList)
    with :? SqliteException as ex ->
      return Error ex
  }

let querySingleOrInsert
  (select: unit -> Task<Result<'a option, SqliteException>>)
  (insert: unit -> Task<Result<'b, SqliteException>>)
  : Task<Result<'a, SqliteException>> =
  task {
    let! existingResult = select ()

    match existingResult with
    | Ok(Some item) -> return Ok item
    | Ok None ->
      let! insertResult = insert ()

      match insertResult with
      | Ok _ ->
        let! insertedResult = select ()

        return
          match insertedResult with
          | Ok(Some item) -> Ok item
          | Ok None -> Error(SqliteException("Failed to retrieve inserted record", 0))
          | Error ex -> Error ex
      | Error ex -> return Error ex
    | Error ex -> return Error ex
  }

type StartupDatabaseState =
  | Missing
  | Ready
  | Migrating
  | Invalid of reason: string

type StartupDatabaseDecision =
  | UseExisting of dbPath: string
  | WaitForMigration of dbPath: string
  | MigrateThisInstance of dbPath: string
  | ExitEarly of dbPath: string * reason: string

let getStartupDatabaseState (dbPath: string) =
  task {
    let! result = DbStartup.getStartupDatabaseState dbPath

    return
      result
      |> Result.map (function
        | DbStartup.Missing -> Missing
        | DbStartup.Ready -> Ready
        | DbStartup.Migrating -> Migrating
        | DbStartup.Invalid reason -> Invalid reason)
  }

let getStartupDatabaseDecision (configuredDirectory: string) (dbFileName: string) =
  task {
    let! result = DbStartup.getStartupDatabaseDecision configuredDirectory dbFileName

    return
      result
      |> Result.map (function
        | DbStartup.UseExisting dbPath -> UseExisting dbPath
        | DbStartup.WaitForMigration dbPath -> WaitForMigration dbPath
        | DbStartup.MigrateThisInstance dbPath -> MigrateThisInstance dbPath
        | DbStartup.ExitEarly(dbPath, reason) -> ExitEarly(dbPath, reason))
  }

let waitForStartupDatabaseReady = DbStartup.waitForStartupDatabaseReady

module MigrationLog =
  let ensureWriteAllowed = MigLib.MigrationLog.ensureWriteAllowed
  let recordInsert = MigLib.MigrationLog.recordInsert
  let recordUpdate = MigLib.MigrationLog.recordUpdate
  let recordDelete = MigLib.MigrationLog.recordDelete

type TxnStep<'a> = DbTransactions.TxnStep<'a>
type DbRuntime = DbTransactions.DbRuntime
type IHasDbRuntime = DbTransactions.IHasDbRuntime
type DbTxnBuilder = DbTransactions.DbTxnBuilder
type TxnBuilder = DbTransactions.TxnBuilder

let dbTxn = DbTransactions.dbTxn
let dbRuntime = DbTransactions.dbRuntime
let txn = DbTransactions.txn
