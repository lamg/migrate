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

module internal Migrate.Execution.Commit

open Microsoft.Data.Sqlite
open Migrate
open Migrate.Types
open DbUtil
open Migrate.SqlGeneration
open NuGet.Versioning
open Spectre.Console
open Migrate.Execution

let replicateInDb (schema: SqlFile) (dbFile: string) =

  let tables = schema.tables |> List.map Table.sqlCreateTable

  let views = schema.views |> List.map View.sqlCreateView

  let inserts = schema.tableSyncs |> List.map InsertInto.sqlInsertInto

  let indexes = schema.indexes |> List.map Index.sqlCreateIndex

  let sql =
    [ tables; views; inserts; indexes ] |> List.concat |> List.concat |> joinSql

  use conn = openConn dbFile
  conn.Open()
  use tx = conn.BeginTransaction()

  try
    Store.Init.initStore conn
    runSql conn sql
    tx.Commit()
  with e ->
    tx.Rollback()
    Print.printRed e.Message

let parseVersion (version: string) = SemanticVersion.TryParse version

let migrateStep (p: Project) (conn: SqliteConnection) : ProposalResult list option =
  let schema = Migrate.DbProject.LoadDbSchema.dbSchema p conn

  Migrate.Calculation.Migration.migration schema p
  |> Option.map (fun statements ->

    statements
    |> List.map (fun s ->
      try
        s.statements |> List.iter (runSql conn)

        { reason = s.reason
          statements = s.statements
          error = None }
      with FailedQuery e ->
        { reason = s.reason
          statements = s.statements
          error = Some $"{e.sql} -> {e.error}" }))

let migrateDb (p: Project) (conn: SqliteConnection) =
  let mutable stop = false
  let mutable steps = ResizeArray<ProposalResult>()
  let mutable last = []
  let mutable i = 0


  while not stop do
    i <- i + 1

    match migrateStep p conn with
    | Some xs when steps.Count > 0 && xs = last -> StaleMigration xs |> raise
    | Some xs ->
      last <- xs
      xs |> List.iter steps.Add
    | None -> stop <- true

  steps |> List.ofSeq

type VersionStatus =
  { shouldMigrate: bool
    projectVersion: SemanticVersion
    dbSchemaVersion: SemanticVersion }

let shouldMigrate (p: Project) (conn: SqliteConnection) =
  let _, schemaVersion =
    Store.Get.getMigrations conn
    |> List.tryHead
    |> Option.map _.migration.schemaVersion
    |> Option.defaultValue "0.0.0"
    |> parseVersion

  let ok, projectVersion = parseVersion p.schemaVersion

  if not ok then
    MalformedProject $"project schema version not valid '{p.schemaVersion}'"
    |> raise

  { shouldMigrate = projectVersion.CompareTo schemaVersion > 0
    projectVersion = projectVersion
    dbSchemaVersion = schemaVersion }

let nothingToMigrate (v: VersionStatus) (quiet: bool) =
  if quiet then
    ()
  else
    Print.printYellow "Nothing to migrate"
    printfn $"Latest project version: {v.projectVersion}"
    printfn $"Latest database version: {v.dbSchemaVersion}"

let createTempDb (schema: SqlFile) (filename: string) =
  let filename = System.IO.Path.GetFileName filename
  let migDir = System.IO.Directory.CreateTempSubdirectory "migrate"
  let testDb = System.IO.Path.Combine(migDir.FullName, filename)
  replicateInDb schema testDb
  testDb

let execManualMigration (p: Project) (conn: SqliteConnection) (sql: string) =
  let schema = Migrate.DbProject.LoadDbSchema.dbSchema p conn

  let tempFile = createTempDb schema p.dbFile
  use tempConn = openConn tempFile

  runSql tempConn sql
  let actual = DbProject.LoadDbSchema.dbSchema { p with dbFile = tempFile } tempConn
  let expected = p.source

  if actual <> expected then
    Print.printRed
      "After running the migration the project and database schemas are not the same. Manual migration failed"

    printfn $"Expected:\n{expected}"
    printfn $"Actual:\n{actual}"
  else
    runSql conn sql

    let m =
      { versionRemarks = p.versionRemarks
        schemaVersion = p.schemaVersion
        date = Print.nowStr ()
        steps =
          [ { reason = Added "Manual migration"
              statements = [ sql ]
              error = None } ] }

    Store.Insert.storeMigration conn m

let manualMigration (p: Project) =
  use conn = openConn p.dbFile
  conn.Open()
  use tx = conn.BeginTransaction()

  try
    match shouldMigrate p conn with
    | vs when vs.shouldMigrate ->
      Print.printYellow "please write the SQL code for the migration and press Ctrl+D"
      let sql = stdin.ReadToEnd()
      Print.printYellow "executing migration…"
      execManualMigration p conn sql
      Print.printGreen "migration executed"
    | vs -> nothingToMigrate vs false

    tx.Commit()
  with e ->
    tx.Rollback()
    Print.printRed e.Message

let migrateAndCommit (p: Project) (quiet: bool) =
  use conn = openConn p.dbFile
  conn.Open()
  use tx = conn.BeginTransaction()

  try
    Store.Init.initStore conn

    match shouldMigrate p conn with
    | vs when vs.shouldMigrate ->
      let s = AnsiConsole.Status()
      s.Spinner <- Spinner.Known.Aesthetic
      s.SpinnerStyle <- Style(foreground = Color.Yellow)
      s.AutoRefresh <- true

      let xs =
        s.StartAsync(
          $"Migrating {p.dbFile}…",
          (fun ctx ->
            task {
              let xs = migrateDb p conn
              return xs
            })
        )
        |> Async.AwaitTask
        |> Async.RunSynchronously

      match xs with
      | [] -> nothingToMigrate vs quiet
      | steps ->
        Store.Insert.storeMigration
          conn
          { steps = steps
            versionRemarks = p.versionRemarks
            schemaVersion = p.schemaVersion
            date = Print.nowStr () }

    | vs -> nothingToMigrate vs quiet

    tx.Commit()
  with e ->
    tx.Rollback()
    Print.printRed e.Message

let commitAmend (p: Project) =
  use conn = openConn p.dbFile
  let m = Store.Get.getMigrations conn |> List.tryHead

  match m with
  | Some v ->
    let vs = shouldMigrate p conn
    conn.Open()
    use tx = conn.BeginTransaction()

    try
      match migrateDb p conn with
      | [] -> nothingToMigrate vs false
      | steps -> Store.Amend.amendLastMigration conn v steps

      tx.Commit()
    with e ->
      tx.Rollback()
      Print.printRed e.Message
  | None -> Print.errPrint "No migrations to amend"

let dryMigration (p: Project) =
  use conn = openConn p.dbFile
  let schema = Migrate.DbProject.LoadDbSchema.dbSchema p conn

  let tempFile = createTempDb schema p.dbFile
  use tempConn = openConn tempFile

  use tx = conn.BeginTransaction()
  use tempTx = tempConn.BeginTransaction()

  try
    Store.Init.initStore conn
    Store.Init.initStore tempConn

    let vs = shouldMigrate p conn
    let xs = migrateDb p tempConn
    tx.Commit()

    match xs with
    | [] -> nothingToMigrate vs false
    | steps ->
      if not vs.shouldMigrate then
        Print.printYellow $"Have in mind since the project and database versions ({vs.projectVersion}) are the same,"
        Print.printYellow "the steps won't be executed. If you want to execute them,"
        Print.printYellow "please increase the project version in the file db.toml."
        Print.printYellow "Otherwise you can use the command `mig commit -a` to amend the last migration"
        printfn ""

      Store.Print.printMigrationIntent steps
  with e ->
    tx.Rollback()
    Print.printRed e.Message
