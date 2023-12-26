module Migrate.Execution.Store.Insert

open System
open System.Security.Cryptography
open Dapper.FSharp.SQLite

open Migrate.Types
open Microsoft.Data.Sqlite
open Migrate.DbUtil
open Migrate.Execution.Store.Types

let hashSteps (dbFile: string) (m: MigrationIntent) =
  let sql =
    let dbFileSteps =
      m.steps
      |> List.mapi (fun i s ->
        let head = $"-- step {i} {s.reason}"

        let body = s.statements |> joinSql

        $"{head}\n{body}")
      |> String.concat "\n"

    $"-- database: {dbFile}\n{dbFileSteps}\n"

  let sqlWithDate =
    $"-- version_remarks: {m.versionRemarks}\n-- migration_date: {m.date}\n--version: {m.schemaVersion}\n{sql}"

  let hasher = SHA256.Create()

  let toString (xs: Byte array) =
    xs |> Convert.ToHexString |> (fun s -> s.ToLower())

  let encode (text: string) = Text.ASCIIEncoding.UTF8.GetBytes text

  let hash = sqlWithDate |> encode |> hasher.ComputeHash |> toString
  hash

let insertSync (conn: SqliteConnection) (q: InsertQuery<'a>) =
  q |> conn.InsertAsync |> Async.AwaitTask |> Async.RunSynchronously |> ignore

let selectLastId (conn: SqliteConnection) =
  let c = conn.CreateCommand()
  c.CommandText <- "SELECT last_insert_rowid()"
  let rd = c.ExecuteReader()

  seq {
    while rd.Read() do
      yield rd.GetInt64 0
  }
  |> Seq.head

let insertNewMigration conn hash dbFile (intent: MigrationIntent) =
  insert {
    into newMigrationTable

    value
      { hash = hash
        versionRemarks = intent.versionRemarks
        date = intent.date
        dbFile = dbFile
        schemaVersion = intent.schemaVersion }
  }
  |> insertSync conn

  let lastInsertedId = selectLastId conn
  lastInsertedId

let insertSteps conn (migrationId: int64) (startIndex: int64) (steps: ProposalResult list) =
  insert {
    into stepTable

    values (
      steps
      |> List.mapi (fun i s ->
        { migrationId = migrationId
          stepIndex = int64 i + startIndex
          sql = s.statements |> joinSqlPretty })
    )
  }
  |> insertSync conn


  insert {
    into stepReasonTable

    values (
      steps
      |> List.mapi (fun i s ->
        let status, entities =
          match s.reason with
          | Added x -> "Added", [ x ]
          | Removed x -> "Removed", [ x ]
          | Changed(x, y) -> "Changed", [ x; y ]

        entities
        |> List.map (fun e ->
          { migrationId = migrationId
            stepIndex = int64 i + startIndex
            status = status
            entity = e }))
      |> List.concat
    )
  }
  |> conn.InsertAsync
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> ignore

  steps
  |> List.iteri (fun i s ->
    s.error
    |> Option.iter (fun e ->
      insert {
        into errorTable

        value
          { migrationId = migrationId
            stepIndex = int64 i + startIndex
            error = e }
      }
      |> conn.InsertAsync
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> ignore))

/// <summary>
/// When writeStore is true the migration is written to the store, otherwise is just printed to stdout
/// </summary>
let storeMigration (conn: SqliteConnection) (m: MigrationIntent) =
  let dbFile = conn.DataSource
  let hash = hashSteps dbFile m

  match m.steps with
  | _ :: _ ->
    let migrationId = insertNewMigration conn hash dbFile m

    insertSteps conn migrationId 0 m.steps
  | [] -> failwith "empty migration reached storeMigration, it should be a non-empty one"
