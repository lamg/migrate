module Migrate.Execution.Store.Amend

open Dapper.FSharp.SQLite
open Migrate.Execution.Store.Types
open Migrate.Types
open Microsoft.Data.Sqlite
open Migrate.Execution.Store.Insert
open Migrate.Print

let amendLastMigration (conn: SqliteConnection) (m: MigrationLog) (xs: ProposalResult list) =
  // amending the last migration consists in the following steps:
  // 0 - insert the new steps after the old ones in the steps tables
  // 1 - update the migration table with the new hash and date
  let startIndex = int64 m.steps.Length
  let migrationId = m.migration.id

  let ys =
    m.steps
    |> List.map (fun s ->
      { reason = s.reason
        error = s.error
        statements = [ s.sql ] })

  let intent =
    { versionRemarks = m.migration.versionRemarks
      steps = ys @ xs
      schemaVersion = m.migration.schemaVersion
      date = nowStr () }

  let dbFile = conn.DataSource
  let hash = hashSteps dbFile intent
  insertSteps conn migrationId startIndex xs

  update {
    for n in migrationTable do
      setColumn n.hash hash
      setColumn n.date intent.date
      where (n.id = migrationId)
  }
  |> conn.UpdateAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> ignore
