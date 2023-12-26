module Migrate.Execution.Store.Get

open Microsoft.Data.Sqlite
open Migrate.Execution.Store.Types
open Dapper.FSharp.SQLite
open Migrate.Types

let getSteps (conn: SqliteConnection) migrationId =
  select {
    for s in stepTable do
      where (s.migrationId = migrationId)
  }
  |> conn.SelectAsync<Step>
  |> Async.AwaitTask
  |> Async.RunSynchronously

let getError (conn: SqliteConnection) (migrationId: int64) (stepIndex: int64) =
  select {
    for e in errorTable do
      where (e.migrationId = migrationId && e.stepIndex = stepIndex)
  }
  |> conn.SelectAsync<Error>
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> Seq.tryHead
  |> Option.map _.error

let getStepReason (conn: SqliteConnection) (migrationId: int64) (stepIndex: int64) =
  select {
    for r in storedStepReasonTable do
      where (r.migrationId = migrationId && r.stepIndex = stepIndex)
      orderBy r.id
  }
  |> conn.SelectAsync<StepReason>
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> Seq.toList
  |> function
    | [ x; y ] when x.status = "Changed" && y.status = "Changed" -> Changed(x.entity, y.entity)
    | [ x ] when x.status = "Added" -> Added x.entity
    | [ x ] when x.status = "Removed" -> Removed x.entity
    | [] ->
      CorruptedStore $"Not found step reason for migrationId = {migrationId} and stepIndex = {stepIndex}"
      |> raise
    | v ->
      CorruptedStore
        $"Expecting 2 entities at most, found {v.Length} for migrationId = {migrationId} and stepIndex = {stepIndex}"
      |> raise

/// <summary>
/// Returns the migrations in the store.
/// Raises FailedOpenDb and FailedOpenStore
/// </summary>
/// <param name="conn"></param>
let getMigrations (conn: SqliteConnection) =
  let migrations =
    select {
      for m in migrationTable do
        orderByDescending m.date
    }
    |> conn.SelectAsync<StoredMigration>
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList


  migrations
  |> List.map (fun (m: StoredMigration) ->
    let steps = getSteps conn m.id

    let stepLogs =
      steps
      |> Seq.map (fun s ->
        { error = getError conn s.migrationId s.stepIndex
          sql = s.sql
          reason = getStepReason conn s.migrationId s.stepIndex })

    let migrationLog =
      { migration = m
        steps = stepLogs |> Seq.toList }

    migrationLog)
