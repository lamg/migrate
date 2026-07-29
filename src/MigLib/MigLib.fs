namespace MigLib

open System.Reflection
open System.Threading.Tasks

/// Public surface used by applications, build.fsx, and the mig CLI.
[<AutoOpen>]
module Api =

  type MigError = Types.MigError
  type CodegenResult = Types.CodegenResult
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
  let withTransactionMode transactionMode db = Types.withTransactionMode transactionMode db
  let txn = Types.txn

  let result = TaskResult.result
  let taskResult = TaskResult.taskResult

  module TxnStep =
    let bind = Runtime.TxnStep.bind
    let map = Runtime.TxnStep.map

  /// Generate typed F# from annotated SQL migrations (same as `mig codegen`).
  let generate (migrationsDir: string) (outputPath: string) (namespaceName: string) : Result<CodegenResult, string> =
    Codegen.Generate.generate migrationsDir outputPath namespaceName

  /// Apply embedded DbUp scripts to the database file.
  let migrateEmbedded (dbPath: string) (assembly: Assembly) : Task<Result<unit, string>> =
    Migrate.migrateEmbedded dbPath assembly

  /// Apply filesystem DbUp scripts to the database file.
  let migrateScripts (dbPath: string) (scriptsDirectory: string) : Task<Result<unit, string>> =
    Migrate.migrateScripts dbPath scriptsDirectory
