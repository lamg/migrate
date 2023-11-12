// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module internal Migrate.MigrationStore

open Migrate.DbProject
open Migrate.Types
open System
open System.Security.Cryptography
open DbUtil
open Microsoft.Data.Sqlite
open Print
open Dapper.FSharp.SQLite

type Migration =
  { id: int64
    hash: string
    versionRemarks: string
    date: string
    dbFile: string
    schemaVersion: string }

type NewMigration =
  { hash: string
    versionRemarks: string
    date: string
    dbFile: string
    schemaVersion: string }

type Step =
  { migrationId: int64
    stepIndex: int64
    reason: string
    sql: string }

type Error =
  { migrationId: int64
    stepIndex: int64
    error: string }

type SqliteMaster = { sql: string }

type StepLog =
  { reason: string
    sql: string
    error: string option }

type MigrationLog =
  { migration: Migration
    steps: StepLog list }

let migrationTable = table'<Migration> $"{LoadDbSchema.migrateTablePrefix}migration"

let newMigrationTable =
  table'<NewMigration> $"{LoadDbSchema.migrateTablePrefix}migration"

let stepTable = table'<Step> $"{LoadDbSchema.migrateTablePrefix}step"

let errorTable = table'<Error> $"{LoadDbSchema.migrateTablePrefix}error"

/// <summary>
/// Opens the database storing the migrations (the store).
/// Raises FailedOpenStore and FailedOpenDb
/// In case the tables dedicated for storing migration metadata don't have
/// the same schema as the one expected by the migration tool, it raises FailedOpenStore
/// </summary>
/// <param name="p"></param>
let openStore (p: Project) =
  let _, referenceStoreSchema =
    System.Reflection.Assembly.GetExecutingAssembly()
    |> fun asm ->
      try
        loadFromRes asm "Migrate.Execution" "store_schema.sql"
      with FailedLoadResFile e ->
        FailedOpenStore e |> raise

  let refSchema =
    match SqlParser.Parser.parseSql p.dbFile referenceStoreSchema with
    | Ok f -> f
    | Error e -> FailedOpenStore e |> raise

  let storeSchema = LoadDbSchema.migrationSchema p

  match storeSchema with
  | None ->
    let conn = openConn p.dbFile
    runSql conn referenceStoreSchema
    conn
  | Some s when s = refSchema -> openConn p.dbFile
  | Some s ->
    FailedOpenStore $"store schema mismatch:\n{p.dbFile} has\n{s}\nwhile mig expects:\n{refSchema} "
    |> raise

let hashSteps (p: Project) (m: MigrationIntent) =
  let migrationDate = nowStr ()

  let sql =
    let dbFileSteps =
      m.steps
      |> List.mapi (fun i s ->
        let head = $"-- step {i} {s.reason}"

        let body = s.statements |> joinSql

        $"{head}\n{body}")
      |> String.concat "\n"

    $"-- database: {p.dbFile}\n{dbFileSteps}\n"

  let sqlWithDate =
    $"-- version_remarks: {m.versionRemarks}\n-- migration_date: {migrationDate}\n--version: {m.schemaVersion}\n{sql}"

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
      yield rd.GetInt32 0
  }
  |> Seq.head

let insertNewMigration conn hash versionRemarks dbFile schemaVersion =
  insert {
    into newMigrationTable

    value
      { hash = hash
        versionRemarks = versionRemarks
        date = nowStr ()
        dbFile = dbFile
        schemaVersion = schemaVersion }
  }
  |> insertSync conn

  let lastInsertedId = selectLastId conn
  lastInsertedId

let insertSteps conn (migrationId: int) (m: MigrationIntent) =
  insert {
    into stepTable

    values (
      m.steps
      |> List.mapi (fun i s ->
        { migrationId = migrationId
          stepIndex = i
          reason = s.reason.ToString()
          sql = s.statements |> joinSqlPretty })
    )
  }
  |> insertSync conn

  m.steps
  |> List.iteri (fun i s ->
    s.error
    |> Option.iter (fun e ->
      insert {
        into errorTable

        value
          { migrationId = migrationId
            stepIndex = i
            error = e }
      }
      |> conn.InsertAsync
      |> Async.AwaitTask
      |> Async.RunSynchronously
      |> ignore))

/// <summary>
/// When writeStore is true the migration is written to the store, otherwise is just printed to stdout
/// </summary>
let storeMigration (p: Project) (m: MigrationIntent) =
  let hash = hashSteps p m
  use conn = openStore p

  match m.steps with
  | _ :: _ ->
    let migrationId =
      insertNewMigration conn hash m.versionRemarks p.dbFile m.schemaVersion

    insertSteps conn migrationId m
  | [] -> failwith "empty migration reached storeMigration, it should be a non-empty one"

/// <summary>
/// Returns the migrations in the store.
/// Raises FailedOpenDb and FailedOpenStore
/// </summary>
/// <param name="p"></param>
let getMigrations (p: Project) =
  use conn = openStore p

  let migrations =
    select {
      for m in migrationTable do
        orderByDescending m.date
    }
    |> conn.SelectAsync<Migration>
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.toList

  let getSteps migrationId =
    select {
      for s in stepTable do
        printfn $"s:{s}"
        where (s.migrationId = migrationId)
    }
    |> conn.SelectAsync<Step>
    |> Async.AwaitTask
    |> Async.RunSynchronously

  let getError (migrationId: int64) (stepIndex: int64) =
    select {
      for e in errorTable do
        where (e.migrationId = migrationId && e.stepIndex = stepIndex)
    }
    |> conn.SelectAsync<Error>
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.tryHead
    |> Option.map (fun e -> e.error)

  migrations
  |> List.map (fun (m: Migration) ->
    let steps = getSteps m.id

    let stepLogs =
      steps
      |> Seq.map (fun s ->
        { error = getError s.migrationId s.stepIndex
          sql = s.sql
          reason = s.reason })

    let migrationLog =
      { migration = m
        steps = stepLogs |> Seq.toList }

    migrationLog)
