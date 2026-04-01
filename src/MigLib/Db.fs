module MigLib.Db

open Microsoft.Data.Sqlite

let Rfc3339UtcNow = DbAttributes.Rfc3339UtcNow

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
