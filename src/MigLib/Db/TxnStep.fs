module MigLib.Db.TxnStep

open System
open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib

/// Represents a transaction-bound asynchronous step that receives the active
/// <see cref="SqliteTransaction"/> and returns either a value or a
/// <see cref="SqliteException"/>.
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

let private continueAsync (next: unit -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> =
  task {
    do! Task.Yield()
    return! next ()
  }

let internal bind (m: TxnStep<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
  fun txn ->
    task {
      let! result = m txn

      match result with
      | Ok value -> return! continueAsync (fun () -> f value txn)
      | Error ex -> return Error ex
    }

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

/// Opens the database at <paramref name="dbPath"/>, starts a transaction,
/// runs <paramref name="body"/>, and commits on success or rolls back on
/// failure.
let internal runTransactionInternal
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
