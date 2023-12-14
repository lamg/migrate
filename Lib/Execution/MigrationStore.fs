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

open System.Text.RegularExpressions
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
/// Initializes tables storing migrations in the database
/// Raises FailedOpenStore and FailedOpenDb
/// In case the tables dedicated for storing migration metadata don't have
/// the same schema as the one expected by the migration tool, it raises FailedOpenStore
/// </summary>
/// <param name="conn"></param>
let initStore (conn: SqliteConnection) =
  let dbFile = conn.DataSource

  let _, referenceStoreSchema =
    System.Reflection.Assembly.GetExecutingAssembly()
    |> fun asm ->
      try
        loadFromRes asm "Migrate.Execution" "store_schema.sql"
      with FailedLoadResFile e ->
        FailedOpenStore e |> raise

  let refSchema =
    match SqlParser.Parser.parseSql dbFile referenceStoreSchema with
    | Ok f -> f
    | Error e -> FailedOpenStore e |> raise

  let storeSchema = LoadDbSchema.migrationSchema conn

  match storeSchema with
  | None -> runSql conn referenceStoreSchema
  | Some s when s = refSchema -> ()
  | Some s ->
    FailedOpenStore $"store schema mismatch:\n{dbFile} has\n{s}\nwhile mig expects:\n{refSchema} "
    |> raise

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
          reason = s.reason.ToString()
          sql = s.statements |> joinSqlPretty })
    )
  }
  |> insertSync conn

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

let parseReason r =
  let added = Regex @"Added ""(.*)"""
  let removed = Regex @"Removed ""(.*)"""
  let changed = Regex @"Changed \(""(.*)"",\s+""(.*)""\)"
  let ra = added.Match r
  let rr = removed.Match r
  let rc = changed.Match r

  match ra.Success, rr.Success, rc.Success with
  | true, _, _ -> Added ra.Groups[1].Value
  | _, true, _ -> Removed rr.Groups[1].Value
  | _, _, true -> Changed(rc.Groups[1].Value, rc.Groups[2].Value)
  | _ -> failwith $"failed to parse reason while amending last migration: {r}"

let appendLastMigration (conn: SqliteConnection) (m: MigrationLog) (xs: ProposalResult list) =
  let startIndex = int64 m.steps.Length

  let ys =
    m.steps
    |> List.map (fun s ->
      { reason = parseReason s.reason
        error = s.error
        statements = [ s.sql ] })

  let intent =
    { versionRemarks = m.migration.versionRemarks
      steps = ys @ xs
      schemaVersion = m.migration.schemaVersion
      date = nowStr () }

  let dbFile = conn.DataSource
  let hash = hashSteps dbFile intent
  insertSteps conn m.migration.id startIndex xs
  let migrationId = m.migration.id

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
