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

  /// Apply snapshot (empty DB) or hop + catalog check.
  /// Apps should call generated <c>{namespace}.Migration.migrate dbPath</c>.
  let migrate (dbPath: string) (expectedSchema: string) (migrationSql: string) : Task<Result<DbTxnBuilder, MigError>> =
    Migrate.migrate dbPath expectedSchema migrationSql
