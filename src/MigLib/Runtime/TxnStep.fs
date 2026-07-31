namespace MigLib.Runtime

module TxnStep =
  open System
  open System.Collections.Concurrent
  open System.IO
  open System.Threading
  open System.Threading.Tasks
  open Microsoft.Data.Sqlite
  open MigLib.Sqlite

  /// Transaction-bound step: receives the active transaction and returns a value or SqliteException.
  type TxnStep<'a> = SqliteTransaction -> Task<Result<'a, SqliteException>>

  let private toSqliteException (error: 'e) : SqliteException =
    match box error with
    | null -> SqliteException("Task<Result<_, _>> returned null error.", 0)
    | :? SqliteException as sqliteError -> sqliteError
    | :? exn as exceptionError -> SqliteException(exceptionError.Message, 0)
    | _ -> SqliteException(string error, 0)

  let internal zero () : TxnStep<unit> = fun _ -> Task.FromResult(Ok())
  let internal result (x: 'a) : TxnStep<'a> = fun _ -> Task.FromResult(Ok x)
  let internal returnFrom (m: TxnStep<'a>) : TxnStep<'a> = m

  /// Immediately fail this step with a SqliteException (error code 0). Used by generated code.
  let fail (message: string) : TxnStep<'a> =
    fun _ -> Task.FromResult(Error(SqliteException(message, 0)))

  let private continueAsync (next: unit -> Task<Result<'a, SqliteException>>) =
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

  let map (f: 'a -> 'b) (m: TxnStep<'a>) : TxnStep<'b> = bind m (fun x -> result (f x))

  let internal bindTask (m: Task<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! value = m
        return! continueAsync (fun () -> f value txn)
      }

  let internal bindTaskResult (m: Task<Result<'a, 'e>>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
    fun txn ->
      task {
        let! result = m

        match result with
        | Ok value -> return! continueAsync (fun () -> f value txn)
        | Error error -> return Error(toSqliteException error)
      }

  let internal combine (m: TxnStep<unit>) (f: TxnStep<'a>) : TxnStep<'a> = bind m (fun () -> f)
  let internal delay (f: unit -> TxnStep<'a>) : TxnStep<'a> = fun txn -> f () txn

  let internal forEach (items: 'a seq) (body: 'a -> TxnStep<unit>) : TxnStep<unit> =
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

  module private Core =
    let resolveDatabasePath (configuredPath: string) : Result<string, string> =
      if String.IsNullOrWhiteSpace configuredPath then
        Error "Configured database path is empty."
      else
        Ok(Path.GetFullPath configuredPath)

    let openSqliteConnection connectionConfig (dbPath: string) =
      openConnectionWithConfig connectionConfig dbPath

  let private transactionGates = ConcurrentDictionary<string, SemaphoreSlim>()

  let private transactionGate resolvedDbPath =
    transactionGates.GetOrAdd(resolvedDbPath, fun _ -> new SemaphoreSlim(1, 1))

  type private ActiveTransaction =
    { resolvedDbPath: string
      transactionMode: SqliteTransactionMode
      transaction: SqliteTransaction }

  let private activeTransaction = AsyncLocal<ActiveTransaction option>()

  let private canReuseTransaction requestedMode activeMode =
    match requestedMode, activeMode with
    | SqliteTransactionMode.ReadOnly, _ -> true
    | SqliteTransactionMode.ReadWrite, SqliteTransactionMode.ReadWrite -> true
    | SqliteTransactionMode.ReadWrite, SqliteTransactionMode.ReadOnly -> false

  let private readWriteInsideReadOnlyError =
    SqliteException("Cannot run a read-write transaction inside an active read-only transaction.", 0)

  let private runInExistingTransaction mapDbError body transaction =
    task {
      try
        return! body transaction
      with :? SqliteException as ex ->
        return Error(mapDbError ex)
    }

  let private runInNewTransaction connectionConfig resolvedDbPath body =
    task {
      use connection = Core.openSqliteConnection connectionConfig resolvedDbPath
      use transaction = connection.BeginTransaction()
      let previous = activeTransaction.Value

      activeTransaction.Value <-
        Some
          { resolvedDbPath = resolvedDbPath
            transactionMode = connectionConfig.transactionMode
            transaction = transaction }

      try
        return! body transaction
      finally
        activeTransaction.Value <- previous
    }

  let private runWithTransactionGate (connectionConfig: ConnectionConfig) resolvedDbPath operation =
    task {
      match connectionConfig.transactionMode with
      | SqliteTransactionMode.ReadOnly -> return! operation ()
      | SqliteTransactionMode.ReadWrite ->
        let gate = transactionGate resolvedDbPath
        do! gate.WaitAsync()

        try
          return! operation ()
        finally
          gate.Release() |> ignore
    }

  /// Opens the database, starts a transaction, runs body, commits on success or rolls back on failure.
  let internal runTransactionInternal
    (connectionConfig: ConnectionConfig)
    (dbPath: string)
    (mapDbError: SqliteException -> 'e)
    (body: SqliteTransaction -> Task<Result<'a, 'e>>)
    : Task<Result<'a, 'e>> =
    task {
      match Core.resolveDatabasePath dbPath with
      | Error message -> return Error(mapDbError (SqliteException(message, 0)))
      | Ok resolvedDbPath ->
        match activeTransaction.Value with
        | Some active when active.resolvedDbPath = resolvedDbPath ->
          if canReuseTransaction connectionConfig.transactionMode active.transactionMode then
            return! runInExistingTransaction mapDbError body active.transaction
          else
            return Error(mapDbError readWriteInsideReadOnlyError)
        | _ ->
          return!
            runWithTransactionGate connectionConfig resolvedDbPath (fun () ->
              task {
                return!
                  runInNewTransaction connectionConfig resolvedDbPath (fun transaction ->
                    task {
                      try
                        let! result = body transaction

                        match result with
                        | Ok value ->
                          transaction.Commit()
                          return Ok value
                        | Error _ ->
                          transaction.Rollback()
                          return result
                      with :? SqliteException as ex ->
                        transaction.Rollback()
                        return Error(mapDbError ex)
                    })
              })
    }
