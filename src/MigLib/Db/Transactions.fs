namespace MigLib

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite

module DbTransactions =
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

    let bind (m: TxnStep<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
      fun txn ->
        task {
          let! result = m txn

          match result with
          | Ok value -> return! f value txn
          | Error ex -> return Error ex
        }

    let bindTask (m: Task<'a>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
      fun txn ->
        task {
          let! value = m
          return! f value txn
        }

    let bindTaskResult (m: Task<Result<'a, 'e>>) (f: 'a -> TxnStep<'b>) : TxnStep<'b> =
      fun txn ->
        task {
          let! result = m

          match result with
          | Ok value -> return! f value txn
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

  let runTransactionInternal
    (dbPath: string)
    (mapDbError: SqliteException -> 'e)
    (body: SqliteTransaction -> Task<Result<'a, 'e>>)
    : Task<Result<'a, 'e>> =
    task {
      match DbCore.resolveDatabasePath dbPath with
      | Error message -> return Error(mapDbError (SqliteException(message, 0)))
      | Ok resolvedDbPath ->
        use connection = DbCore.openSqliteConnection resolvedDbPath
        use transaction = connection.BeginTransaction()
        let! readinessResult = DbRecording.ensureNewDatabaseReadyForTransactions transaction

        match readinessResult with
        | Error ex ->
          transaction.Rollback()
          return Error(mapDbError ex)
        | Ok() ->
          let! mode = DbRecording.detectMigrationMode transaction

          let context =
            { DbCore.TxnContext.tx = transaction
              DbCore.TxnContext.mode = mode
              DbCore.TxnContext.writes = ResizeArray() }

          let previousContext = DbCore.txnContext.Value
          DbCore.txnContext.Value <- Some context

          try
            try
              let! result = body transaction

              match result with
              | Ok value ->
                let! flushResult = DbRecording.flushRecordedWrites context

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
            DbCore.txnContext.Value <- previousContext
    }

  type DbRuntime(dbPath: string) =
    member _.DbPath = dbPath

    member _.RunInTransaction
      (mapDbError: SqliteException -> 'e)
      (body: SqliteTransaction -> Task<Result<'a, 'e>>)
      : Task<Result<'a, 'e>> =
      runTransactionInternal dbPath mapDbError body

  type IHasDbRuntime =
    abstract DbRuntime: DbRuntime

  type DbTxnBuilder(dbPath: string) =
    member _.DbPath = dbPath
    member _.DbRuntime = DbRuntime dbPath

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

  type TxnBuilder() =
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

  let dbTxn dbPath = DbTxnBuilder dbPath
  let dbRuntime dbPath = DbRuntime dbPath
  let txn = TxnBuilder()
