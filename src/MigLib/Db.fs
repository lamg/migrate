/// Database transaction management utilities for generated code
module migrate.Db

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Execute an action within a transaction, handling commit/rollback automatically
let WithTransaction
  (conn: SqliteConnection)
  (action: SqliteTransaction -> Result<'T, SqliteException>)
  : Result<'T, SqliteException> =
  let tx = conn.BeginTransaction()

  try
    match action tx with
    | Ok result ->
      tx.Commit()
      Ok result
    | Error ex ->
      tx.Rollback()
      Error ex
  with :? SqliteException as ex ->
    tx.Rollback()
    Error ex

/// Computation expression builder for database transactions with Result monad
type TxnBuilder(dbPath: string) =

  /// Bind a transaction function and continue with another
  member _.Bind
    (m: SqliteTransaction -> Result<'T, SqliteException>, f: 'T -> SqliteTransaction -> Result<'U, SqliteException>)
    : SqliteTransaction -> Result<'U, SqliteException> =
    fun (tx: SqliteTransaction) ->
      match m tx with
      | Ok value -> f value tx
      | Error ex -> Error ex

  /// Return a value wrapped in Ok
  member _.Return(x: 'T) : SqliteTransaction -> Result<'T, SqliteException> = fun _ -> Ok x

  /// Return a transaction function as-is
  member _.ReturnFrom
    (m: SqliteTransaction -> Result<'T, SqliteException>)
    : SqliteTransaction -> Result<'T, SqliteException> =
    m

  /// Execute the transaction with automatic connection opening, transaction management, and cleanup
  member _.Run(action: SqliteTransaction -> Result<'T, SqliteException>) : Result<'T, SqliteException> =
    try
      // Convert database path to SQLite connection string
      let connString = $"Data Source={dbPath}"
      use conn = new SqliteConnection(connString)
      conn.Open()
      WithTransaction conn action
    with :? SqliteException as ex ->
      Error ex

/// Create a transaction computation expression that accepts a database file path
let txn (dbPath: string) = TxnBuilder dbPath

/// Computation expression builder for async database transactions with Task<Result> monad
type TaskTxnBuilder(dbPath: string) =

  /// Bind a transaction function and continue with another
  member _.Bind
    (
      m: SqliteTransaction -> Task<Result<'T, SqliteException>>,
      f: 'T -> SqliteTransaction -> Task<Result<'U, SqliteException>>
    ) : SqliteTransaction -> Task<Result<'U, SqliteException>> =
    fun (tx: SqliteTransaction) ->
      task {
        match! m tx with
        | Ok value -> return! f value tx
        | Error ex -> return Error ex
      }

  /// Return a value wrapped in Ok
  member _.Return(x: 'T) : SqliteTransaction -> Task<Result<'T, SqliteException>> = fun _ -> Task.FromResult(Ok x)

  /// Return a transaction function as-is
  member _.ReturnFrom
    (m: SqliteTransaction -> Task<Result<'T, SqliteException>>)
    : SqliteTransaction -> Task<Result<'T, SqliteException>> =
    m

  member _.Zero() : SqliteTransaction -> Task<Result<unit, SqliteException>> = fun _ -> Task.FromResult(Ok())

  member _.Delay
    (f: unit -> SqliteTransaction -> Task<Result<'T, SqliteException>>)
    : SqliteTransaction -> Task<Result<'T, SqliteException>> =
    fun tx -> f () tx

  member _.Combine
    (
      m: SqliteTransaction -> Task<Result<unit, SqliteException>>,
      f: SqliteTransaction -> Task<Result<'T, SqliteException>>
    ) : SqliteTransaction -> Task<Result<'T, SqliteException>> =
    fun (tx: SqliteTransaction) ->
      task {
        match! m tx with
        | Ok() -> return! f tx
        | Error ex -> return Error ex
      }

  member _.For
    (sequence: seq<'T>, body: 'T -> SqliteTransaction -> Task<Result<unit, SqliteException>>)
    : SqliteTransaction -> Task<Result<unit, SqliteException>> =
    fun (tx: SqliteTransaction) ->
      task {
        use enumerator = sequence.GetEnumerator()
        let mutable lastError: SqliteException option = None
        let mutable hasMore = enumerator.MoveNext()

        while hasMore && lastError.IsNone do
          match! body enumerator.Current tx with
          | Ok() -> hasMore <- enumerator.MoveNext()
          | Error err -> lastError <- Some err

        match lastError with
        | Some err -> return Error err
        | None -> return Ok()
      }

  /// Execute the transaction with automatic connection opening, transaction management, and cleanup
  member _.Run(action: SqliteTransaction -> Task<Result<'T, SqliteException>>) : Task<Result<'T, SqliteException>> =
    task {
      try
        // Convert database path to SQLite connection string
        let connString = $"Data Source={dbPath}"
        use conn = new SqliteConnection(connString)
        conn.Open()
        let tx = conn.BeginTransaction()

        try
          match! action tx with
          | Ok result ->
            tx.Commit()
            return Ok result
          | Error ex ->
            tx.Rollback()
            return Error ex
        with :? SqliteException as ex ->
          tx.Rollback()
          return Error ex
      with :? SqliteException as ex ->
        return Error ex
    }

/// Create an async transaction computation expression that accepts a database file path
let taskTxn (dbPath: string) = TaskTxnBuilder dbPath
