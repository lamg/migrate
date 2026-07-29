namespace MigLib

module Types =
  open System
  open System.Threading.Tasks
  open Microsoft.Data.Sqlite
  open MigLib.Runtime.TxnStep
  open MigLib.Sqlite

  [<RequireQualifiedAccess>]
  type MigError =
    | Message of string
    | Sqlite of SqliteException
    | Other of Exception

  let private runTxnStepAsMigError dbPath connectionConfig (step: TxnStep<'a>) : Task<Result<'a, MigError>> =
    runTransactionInternal connectionConfig dbPath MigError.Sqlite (fun tx ->
      task {
        let! result = step tx
        return result |> Result.mapError MigError.Sqlite
      })

  /// Provides transaction execution services for a fixed database path.
  type DbRuntime internal (dbPath: string, connectionConfig: ConnectionConfig) =
    new(dbPath: string) = DbRuntime(dbPath, defaultConnectionConfig)

    member _.DbPath = dbPath
    member internal _.ConnectionConfig = connectionConfig

    member _.RunInTransaction
      (mapDbError: SqliteException -> 'e)
      (body: SqliteTransaction -> Task<Result<'a, 'e>>)
      : Task<Result<'a, 'e>> =
      runTransactionInternal connectionConfig dbPath mapDbError body

  type IHasDbRuntime =
    abstract DbRuntime: DbRuntime

  /// Computation expression builder for running transaction steps against a fixed database path.
  type DbTxnBuilder internal (dbPath: string, connectionConfig: ConnectionConfig) =
    new(dbPath: string) = DbTxnBuilder(dbPath, defaultConnectionConfig)

    member _.DbPath = dbPath
    member _.DbRuntime = DbRuntime(dbPath, connectionConfig)
    member internal _.ConnectionConfig = connectionConfig
    member internal _.WithConnectionConfig connectionConfig = DbTxnBuilder(dbPath, connectionConfig)

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

  /// Computation expression builder for composing reusable transaction steps.
  type TxnBuilder() =
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

  let dbTxn dbPath = DbTxnBuilder dbPath
  let dbRuntime dbPath = DbRuntime dbPath

  let withTransactionMode transactionMode (db: DbTxnBuilder) =
    db.WithConnectionConfig
      { db.ConnectionConfig with
          transactionMode = transactionMode
      }

  let readOnlyDbTxn dbPath =
    dbTxn dbPath |> withTransactionMode SqliteTransactionMode.ReadOnly

  let readOnlyDbRuntime dbPath =
    DbRuntime(
      dbPath,
      { defaultConnectionConfig with
          transactionMode = SqliteTransactionMode.ReadOnly
      }
    )

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

  let withBusyTimeout timeout (db: DbTxnBuilder) =
    validateBusyTimeout timeout

    db.WithConnectionConfig
      { db.ConnectionConfig with
          busyTimeout = Some timeout
      }

  let txn = TxnBuilder()

