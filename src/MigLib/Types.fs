module MigLib.Types

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Schema.Types
open MigLib.Db.TxnStep

type ResolvedGeneratedSchemaModule =
  { schema: SqlFile
    schemaHash: string
    dbApp: string
    defaultDbInstance: string }

[<RequireQualifiedAccess>]
type MigError =
  | Regular of string
  | Sqlite of SqliteException
  | Other of Exception

type SqlFile = MigLib.Schema.Types.SqlFile

type InitResult =
  { newDbPath: string; seededRows: int64 }

type CodegenResult =
  { outputPath: string
    generatedModuleName: string
    generatedFiles: string list }

type PlanResult =
  { sourceDbPath: string option
    targetDbPath: string
    canMigrate: bool
    supportedDifferences: string list
    unsupportedDifferences: string list }

let private runTxnStepAsMigError dbPath (step: TxnStep<'a>) : Task<Result<'a, MigError>> =
  runTransactionInternal dbPath MigError.Sqlite (fun tx ->
    task {
      let! result = step tx
      return result |> Result.mapError MigError.Sqlite
    })

/// Provides transaction execution services for a fixed database path.
type DbRuntime(dbPath: string) =
  /// Gets the database path used by this runtime.
  member _.DbPath = dbPath

  /// Runs <paramref name="body"/> inside a transaction against this runtime's
  /// database path.
  member _.RunInTransaction
    (mapDbError: SqliteException -> 'e)
    (body: SqliteTransaction -> Task<Result<'a, 'e>>)
    : Task<Result<'a, 'e>> =
    runTransactionInternal dbPath mapDbError body

/// Exposes a <see cref="DbRuntime"/> for a value that owns database access.
type IHasDbRuntime =
  /// Gets the runtime used to execute transactions.
  abstract DbRuntime: DbRuntime

/// Computation expression builder for running transaction steps against a fixed
/// database path.
/// Supports binding <see cref="TxnStep{T}"/>, <see cref="Task{TResult}"/>, and
/// <c>Task&lt;Result&lt;_, _&gt;&gt;</c> values.
type DbTxnBuilder(dbPath: string) =
  /// Gets the database path used by this builder.
  member _.DbPath = dbPath

  /// Gets the reusable runtime bound to this builder's database path.
  member _.DbRuntime = DbRuntime dbPath

  /// Runs a composed transaction step against this builder's database path.
  member _.Run(f: TxnStep<'a>) : Task<Result<'a, MigError>> = runTxnStepAsMigError dbPath f

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

/// Shared transaction computation expression builder for composing reusable
/// <see cref="TxnStep{T}"/> values before binding them to a concrete database
/// path.
let txn = TxnBuilder()

type MigrateResult =
  { db: DbTxnBuilder
    newDbPath: string
    archivedOldDbPath: string option
    copiedTables: int
    copiedRows: int64 }

type StatusResult =
  { currentDbPath: string option
    archivedDbPaths: string list
    needsMigration: bool }

type ResetResult =
  { restoredDbPath: string option
    removedCurrentDbPath: string option }

type ProgReport = string -> Task<unit>

type ResolvedProject =
  { targetDbPath: string
    targetSchema: ResolvedGeneratedSchemaModule
    sourceDbPath: string option
    sourceDbSchema: SqlFile option
    archiveDir: string }
