namespace Mig.DeclarativeMigrations

open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.Types

module internal DrainReplayTypes =
  type ReplayOperation =
    | Insert
    | Update
    | Delete

  type MigrationLogEntry =
    { id: int64
      txnId: int64
      ordering: int64
      operation: ReplayOperation
      sourceTable: string
      rowData: Map<string, Expr> }

  let toSqliteError (message: string) = SqliteException(message, 0)
