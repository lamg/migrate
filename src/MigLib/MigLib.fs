namespace MigLib

open System.Threading.Tasks

/// Public surface used by applications and build scripts (runtime only).
/// Codegen lives in the MigLib.Codegen package.
[<AutoOpen>]
module Api =

  type MigError = Types.MigError
  type SqliteJournalMode = Sqlite.SqliteJournalMode
  type SqliteTransactionMode = Sqlite.SqliteTransactionMode

  /// Reusable transaction-bound database operation.
  type TxnStep<'a> = Runtime.TxnStep.TxnStep<'a>

  type DbRuntime = Types.DbRuntime
  type IHasDbRuntime = Types.IHasDbRuntime
  type DbTxnBuilder = Types.DbTxnBuilder
  type TxnBuilder = Types.TxnBuilder

  let dbTxn dbPath = Types.dbTxn dbPath
  let dbRuntime dbPath = Types.dbRuntime dbPath
  let readOnlyDbTxn dbPath = Types.readOnlyDbTxn dbPath
  let readOnlyDbRuntime dbPath = Types.readOnlyDbRuntime dbPath
  let withJournalMode journalMode db = Types.withJournalMode journalMode db
  let withBusyTimeout timeout db = Types.withBusyTimeout timeout db

  let withTransactionMode transactionMode db =
    Types.withTransactionMode transactionMode db

  let txn = Types.txn

  module TxnStep =
    let bind = Runtime.TxnStep.bind
    let map = Runtime.TxnStep.map
    let fail = Runtime.TxnStep.fail

  /// Apply ordered named migration scripts to the database file.
  /// Script names are SchemaVersions keys; list order is apply order.
  /// Prefer the list generated into `{namespace}.Migrations.scripts` by `mig codegen`
  /// (ordinary F# string constants compiled into the app — AOT-friendly; no resources or reflection).
  let migrateScripts (dbPath: string) (scripts: (string * string) list) : Task<Result<unit, string>> =
    Migrate.migrateScripts dbPath scripts
