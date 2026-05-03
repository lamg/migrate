module MigLib.Db.Transactions

open System
open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib

/// Represents a transaction-bound asynchronous step that receives the active
/// <see cref="SqliteTransaction"/> and returns either a value or a
/// <see cref="SqliteException"/>.
type TxnStep<'a> = SqliteTransaction -> Task<Result<'a, SqliteException>>

module private TxnStep =
  let private toSqliteException (error: 'e) : SqliteException =
    match box error with
    | null -> SqliteException("Task<Result<_, _>> returned null error.", 0)
    | :? SqliteException as sqliteError -> sqliteError
    | :? exn as exceptionError -> SqliteException(exceptionError.Message, 0)
    | _ -> SqliteException(string error, 0)

  let zero () : TxnStep<unit> = fun _ -> Task.FromResult(Ok())
  let result (x: 'a) : TxnStep<'a> = fun _ -> Task.FromResult(Ok x)
  let returnFrom (m: TxnStep<'a>) : TxnStep<'a> = m

  let private continueAsync (next: unit -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> =
    task {
      do! Task.Yield()
      return! next ()
    }

  let bind (m: TxnStep<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! result = m txn

        match result with
        | Ok value -> return! continueAsync (fun () -> f value txn)
        | Error ex -> return Error ex
      }

  let bindTask (m: Task<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! value = m
        return! continueAsync (fun () -> f value txn)
      }

  let bindTaskResult (m: Task<Result<'a, 'e>>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! result = m

        match result with
        | Ok value -> return! continueAsync (fun () -> f value txn)
        | Error error -> return Error(toSqliteException error)
      }

  let combine (m: TxnStep<unit>) (f: TxnStep<'a>) : TxnStep<'a> = bind m (fun () -> f)
  let delay (f: unit -> TxnStep<'a>) : TxnStep<'a> = fun txn -> f () txn

  let forEach (items: 'a seq) (body: 'a -> TxnStep<unit>) : TxnStep<unit> =
    fun txn ->
      task {
        let mutable error = None

        use enumerator = items.GetEnumerator()

        while error.IsNone && enumerator.MoveNext() do
          let! result = body enumerator.Current txn

          match result with
          | Ok() -> ()
          | Error ex -> error <- Some ex

        match error with
        | Some ex -> return Error ex
        | None -> return Ok()
      }

/// Opens the database at <paramref name="dbPath"/>, starts a transaction,
/// runs <paramref name="body"/>, and commits on success or rolls back on
/// failure.
let runTransactionInternal
  (dbPath: string)
  (mapDbError: SqliteException -> 'e)
  (body: SqliteTransaction -> Task<Result<'a, 'e>>)
  : Task<Result<'a, 'e>> =
  task {
    match Db.Core.resolveDatabasePath dbPath with
    | Error message -> return Error(mapDbError (SqliteException(message, 0)))
    | Ok resolvedDbPath ->
      use connection = Db.Core.openSqliteConnection resolvedDbPath
      use transaction = connection.BeginTransaction()
      let! readinessResult = Db.Recording.ensureNewDatabaseReadyForTransactions transaction

      match readinessResult with
      | Error ex ->
        transaction.Rollback()
        return Error(mapDbError ex)
      | Ok() ->
        let! mode = Db.Recording.detectMigrationMode transaction

        let context =
          { Db.Core.TxnContext.tx = transaction
            Db.Core.TxnContext.mode = mode
            Db.Core.TxnContext.writes = ResizeArray() }

        let previousContext = Db.Core.txnContext.Value
        Db.Core.txnContext.Value <- Some context

        try
          try
            let! result = body transaction

            match result with
            | Ok value ->
              let! flushResult = Db.Recording.flushRecordedWrites context

              match flushResult with
              | Ok() ->
                transaction.Commit()
                return Ok value
              | Error ex ->
                transaction.Rollback()
                return Error(mapDbError ex)
            | Error _ ->
              transaction.Rollback()
              return result
          with :? SqliteException as ex ->
            transaction.Rollback()
            return Error(mapDbError ex)
        finally
          Db.Core.txnContext.Value <- previousContext
  }

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
  member _.Run(f: TxnStep<'a>) : Task<Result<'a, SqliteException>> = runTransactionInternal dbPath id f
  member _.Zero() : TxnStep<unit> = TxnStep.zero ()
  member _.Return(x: 'a) : TxnStep<'a> = TxnStep.result x
  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = TxnStep.returnFrom m
  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bind m f
  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTask m f
  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTaskResult m f
  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = TxnStep.combine m f
  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = TxnStep.delay f
  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = TxnStep.forEach items body

/// Computation expression builder for composing reusable transaction steps
/// independently of any concrete database path.
/// Supports binding <see cref="TxnStep{T}"/>, <see cref="Task{TResult}"/>, and
/// <c>Task&lt;Result&lt;_, _&gt;&gt;</c> values.
type TxnBuilder() =
  /// Returns the composed transaction step without executing it.
  member _.Run(f: TxnStep<'a>) : TxnStep<'a> = f
  member _.Zero() : TxnStep<unit> = TxnStep.zero ()
  member _.Return(x: 'a) : TxnStep<'a> = TxnStep.result x
  member _.ReturnFrom(m: TxnStep<'a>) : TxnStep<'a> = TxnStep.returnFrom m
  member _.Bind(m: TxnStep<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bind m f
  member _.Bind(m: Task<'a>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTask m f
  member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> TxnStep<'b>) : TxnStep<'b> = TxnStep.bindTaskResult m f
  member _.Combine(m: TxnStep<unit>, f: TxnStep<'a>) : TxnStep<'a> = TxnStep.combine m f
  member _.Delay(f: unit -> TxnStep<'a>) : TxnStep<'a> = TxnStep.delay f
  member _.For(items: 'a seq, body: 'a -> TxnStep<unit>) : TxnStep<unit> = TxnStep.forEach items body

/// Creates a transaction computation expression builder bound to
/// <paramref name="dbPath"/>.
let dbTxn dbPath = DbTxnBuilder dbPath

/// Creates a reusable database runtime bound to <paramref name="dbPath"/>.
let dbRuntime dbPath = DbRuntime dbPath

/// Shared transaction computation expression builder for composing reusable
/// <see cref="TxnStep{T}"/> values before binding them to a concrete database
/// path.
let txn = TxnBuilder()
