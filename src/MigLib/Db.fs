/// Database transaction management utilities for generated code
module migrate.Db

open System
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
type TxnBuilder(conn: SqliteConnection) =

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

  /// Execute the transaction with automatic commit/rollback
  member _.Run(action: SqliteTransaction -> Result<'T, SqliteException>) : Result<'T, SqliteException> =
    WithTransaction conn action

/// Create a transaction computation expression
let txn (conn: SqliteConnection) = TxnBuilder conn
