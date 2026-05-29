module MigLib.Types

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Schema.Types
open MigLib.Runtime.TxnStep

type ResolvedGeneratedSchemaModule =
  {
    schema: SqlFile
    schemaHash: string
    dbApp: string
    defaultDbInstance: string
  }

[<RequireQualifiedAccess>]
type MigError =
  | Regular of string
  | Sqlite of SqliteException
  | Other of Exception

type SqlFile = MigLib.Schema.Types.SqlFile

type InitResult =
  { newDbPath: string; seededRows: int64 }

type CodegenResult =
  {
    outputPath: string
    generatedModuleName: string
    generatedFiles: string list
  }

type PlanResult =
  {
    sourceDbPath: string option
    targetDbPath: string
    canMigrate: bool
    supportedDifferences: string list
    unsupportedDifferences: string list
  }

type SqliteJournalMode = MigLib.SqliteJournalMode

type SqliteTransactionMode = MigLib.SqliteTransactionMode

let private runTxnStepAsMigError dbPath connectionConfig (step: TxnStep<'a>) : Task<Result<'a, MigError>> =
  runTransactionInternal connectionConfig dbPath MigError.Sqlite (fun tx ->
    task {
      let! result = step tx
      return result |> Result.mapError MigError.Sqlite
    })

/// Provides transaction execution services for a fixed database path.
type DbRuntime internal (dbPath: string, connectionConfig: MigLib.Sqlite.ConnectionConfig) =
  new(dbPath: string) = DbRuntime(dbPath, MigLib.Sqlite.defaultConnectionConfig)

  /// Gets the database path used by this runtime.
  member _.DbPath = dbPath

  member internal _.ConnectionConfig = connectionConfig

  /// Runs <paramref name="body"/> inside a transaction against this runtime's
  /// database path.
  member _.RunInTransaction
    (mapDbError: SqliteException -> 'e)
    (body: SqliteTransaction -> Task<Result<'a, 'e>>)
    : Task<Result<'a, 'e>> =
    runTransactionInternal connectionConfig dbPath mapDbError body

/// Exposes a <see cref="DbRuntime"/> for a value that owns database access.
type IHasDbRuntime =
  /// Gets the runtime used to execute transactions.
  abstract DbRuntime: DbRuntime

/// Computation expression builder for running transaction steps against a fixed
/// database path.
/// Supports binding <see cref="TxnStep{T}"/>, <see cref="Task{TResult}"/>, and
/// <c>Task&lt;Result&lt;_, _&gt;&gt;</c> values.
type DbTxnBuilder internal (dbPath: string, connectionConfig: MigLib.Sqlite.ConnectionConfig) =
  new(dbPath: string) = DbTxnBuilder(dbPath, MigLib.Sqlite.defaultConnectionConfig)

  /// Gets the database path used by this builder.
  member _.DbPath = dbPath

  /// Gets the reusable runtime bound to this builder's database path.
  member _.DbRuntime = DbRuntime(dbPath, connectionConfig)

  member internal _.ConnectionConfig = connectionConfig

  member internal _.WithConnectionConfig connectionConfig = DbTxnBuilder(dbPath, connectionConfig)

  /// Runs a composed transaction step against this builder's database path.
  member _.Run(f: TxnStep<'a>) : Task<Result<'a, MigError>> =
    runTxnStepAsMigError dbPath connectionConfig f

  member _.Zero() : TxnStep<unit> = zero ()
  member _.Return(x: 'a) : TxnStep<'a> = result x
  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = returnFrom m
  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bind m f
  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bindTask m f
  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bindTaskResult m f
  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = combine m f
  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = delay f
  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = forEach items body

/// Computation expression builder for composing reusable transaction steps
/// independently of any concrete database path.
/// Supports binding <see cref="TxnStep{T}"/>, <see cref="Task{TResult}"/>, and
/// <c>Task&lt;Result&lt;_, _&gt;&gt;</c> values.
type TxnBuilder() =
  /// Returns the composed transaction step without executing it.
  member _.Run(f: TxnStep<'a>) : TxnStep<'a> = f
  member _.Zero() : TxnStep<unit> = zero ()
  member _.Return(x: 'a) : TxnStep<'a> = result x
  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = returnFrom m
  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bind m f
  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bindTask m f
  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = bindTaskResult m f
  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = combine m f
  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = delay f
  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = forEach items body

/// Creates a transaction computation expression builder bound to
/// <paramref name="dbPath"/>.
let dbTxn dbPath = DbTxnBuilder dbPath

/// Creates a reusable database runtime bound to <paramref name="dbPath"/>.
let dbRuntime dbPath = DbRuntime dbPath

/// Returns a new transaction builder that uses the selected SQLite transaction
/// mode when opening transaction connections.
let withTransactionMode transactionMode (db: DbTxnBuilder) =
  db.WithConnectionConfig
    { db.ConnectionConfig with
        transactionMode = transactionMode
    }

/// Creates a read-only transaction computation expression builder bound to
/// <paramref name="dbPath"/>.
let readOnlyDbTxn dbPath =
  dbTxn dbPath |> withTransactionMode SqliteTransactionMode.ReadOnly

/// Creates a read-only reusable database runtime bound to <paramref name="dbPath"/>.
let readOnlyDbRuntime dbPath =
  DbRuntime(
    dbPath,
    { MigLib.Sqlite.defaultConnectionConfig with
        transactionMode = SqliteTransactionMode.ReadOnly
    }
  )

/// Returns a new transaction builder that applies the selected SQLite journal
/// mode when opening transaction connections.
let withJournalMode journalMode (db: DbTxnBuilder) =
  db.WithConnectionConfig
    { db.ConnectionConfig with
        journalMode = journalMode
    }

let private validateBusyTimeout (timeout: TimeSpan) =
  if timeout < TimeSpan.Zero then
    raise (ArgumentOutOfRangeException(nameof timeout, timeout, "Busy timeout cannot be negative."))

  if timeout.TotalSeconds > float Int32.MaxValue then
    raise (ArgumentOutOfRangeException(nameof timeout, timeout, "Busy timeout is too large."))

/// Returns a new transaction builder with the SQLite busy timeout applied to
/// transaction connections.
let withBusyTimeout timeout (db: DbTxnBuilder) =
  validateBusyTimeout timeout

  db.WithConnectionConfig
    { db.ConnectionConfig with
        busyTimeout = Some timeout
    }

/// Shared transaction computation expression builder for composing reusable
/// <see cref="TxnStep{T}"/> values before binding them to a concrete database
/// path.
let txn = TxnBuilder()

type MigrateResult =
  {
    db: DbTxnBuilder
    newDbPath: string
    archivedOldDbPath: string option
    copiedTables: int
    copiedRows: int64
  }

type StatusResult =
  {
    currentDbPath: string option
    archivedDbPaths: string list
    needsMigration: bool
  }

type ResetResult =
  {
    restoredDbPath: string option
    removedCurrentDbPath: string option
  }

type ProgReport = string -> Task<unit>

type ResolvedProject =
  {
    targetDbPath: string
    targetSchema: ResolvedGeneratedSchemaModule
    sourceDbPath: string option
    sourceDbSchema: SqlFile option
    archiveDir: string
  }
