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

open Migrate
open Migrate.Types
open DbUtil
open SqlParser.Types
open Migrate.SqlGeneration
open NuGet.Versioning

let replicateInDb (schema: SqlFile) (dbFile: string) =

  let tables = schema.tables |> List.map Table.sqlCreateTable

  let views = schema.views |> List.map View.sqlCreateView

  let inserts = schema.inserts |> List.map InsertInto.sqlInsertInto

  let indexes = schema.indexes |> List.map Index.sqlCreateIndex

  let sql =
    [ tables; views; inserts; indexes ] |> List.concat |> List.concat |> joinSql

  use conn = openConn dbFile
  runSql conn sql

let parseVersion (version: string) = SemanticVersion.TryParse version

let migrateStep (p: Project) : ProposalResult list option =
  try
    let schema = Migrate.DbProject.LoadDbSchema.dbSchema p

    Migrate.Calculation.Migration.migration schema p
    |> Option.map (fun statements ->

      use conn = openConn p.dbFile

      statements
      |> List.map (fun s ->
        match runSqlTx conn s.statements with
        | Ok() ->
          { reason = s.reason
            statements = s.statements
            error = None }
        | Error e ->
          { reason = s.reason
            statements = s.statements
            error = Some e }))
  with
  | FailedQuery e ->
    printQueryErr e
    None
  | e ->
    Print.printRed $"got error\n{e.Message}"
    None

let printProgress (n: int) =
  let prog = [| "/"; "-"; "\\"; "|" |]
  let i = n % prog.Length
  printf $"\r{prog[i]}"

let migrateDb (p: Project) =
  let mutable stop = false
  let mutable steps = ResizeArray<ProposalResult>()
  let mutable last = []
  let mutable i = 0

  while not stop do
    printProgress i
    i <- i + 1

    match migrateStep p with
    | Some xs when steps.Count > 0 && xs = last -> StaleMigration xs |> raise
    | Some xs ->
      last <- xs
      xs |> List.iter steps.Add
    | None ->
      printfn "\r"
      stop <- true

  steps |> List.ofSeq

type VersionStatus =
  { shouldMigrate: bool
    projectVersion: SemanticVersion
    dbSchemaVersion: SemanticVersion }

let shouldMigrate (p: Project) =
  let _, schemaVersion =
    MigrationStore.getMigrations p
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

let nothingToMigrate
  ({ projectVersion = pv
     dbSchemaVersion = sv }: VersionStatus)
  =
  Print.printYellow "Nothing to migrate"
  printfn $"Latest project version: {pv}"
  printfn $"Latest database version: {sv}"

let createTempDb (schema: SqlFile) (dbFile: string) =
  let filename = System.IO.Path.GetFileName dbFile
  let migDir = System.IO.Directory.CreateTempSubdirectory "migrate"
  let testDb = System.IO.Path.Combine(migDir.FullName, filename)
  replicateInDb schema testDb
  testDb

let execManualMigration (p: Project) (sql: string) =
  let schema = Migrate.DbProject.LoadDbSchema.dbSchema p

  let tempFile = createTempDb schema p.dbFile

  match runSqlTx (openConn tempFile) [ sql ] with
  | Ok() ->
    let actual = DbProject.LoadDbSchema.dbSchema { p with dbFile = tempFile }
    let expected = p.source

    if actual <> expected then
      Print.printRed
        "After running the migration the project and database schemas are not the same. Manual migration failed"

      printfn $"Expected:\n{expected}"
      printfn $"Actual:\n{actual}"

    else
      match runSqlTx (openConn p.dbFile) [ sql ] with
      | Ok() ->
        let m =
          { versionRemarks = p.versionRemarks
            schemaVersion = p.schemaVersion
            date = Print.nowStr ()
            steps =
              [ { reason = Added "Manual migration"
                  statements = [ sql ]
                  error = None } ] }

        MigrationStore.storeMigration p m
      | Error e -> Print.printRed e
  | Error e -> Print.printRed e

let manualMigration (p: Project) =
  match shouldMigrate p with
  | vs when vs.shouldMigrate ->
    Print.printYellow "please write the SQL code for the migration and press Ctrl+D"
    let sql = stdin.ReadToEnd()
    Print.printYellow "executing migration…"
    execManualMigration p sql
    Print.printGreen "migration executed successfully"
  | vs -> nothingToMigrate vs

let migrateAndCommit (p: Project) =
  match shouldMigrate p with
  | vs when vs.shouldMigrate ->
    match migrateDb p with
    | [] -> nothingToMigrate vs
    | steps ->
      MigrationStore.storeMigration
        p
        { steps = steps
          versionRemarks = p.versionRemarks
          schemaVersion = p.schemaVersion
          date = Print.nowStr () }
  | vs -> nothingToMigrate vs

let commitAmend (p: Project) =
  let m = MigrationStore.getMigrations p |> List.tryHead

  match m with
  | Some v ->
    let vs = shouldMigrate p

    match migrateDb p with
    | [] -> nothingToMigrate vs
    | steps -> MigrationStore.appendLastMigration p v steps
  | None -> Print.errPrint "No migrations to amend"

let dryMigration (p: Project) =
  let schema = Migrate.DbProject.LoadDbSchema.dbSchema p

  let tempFile = createTempDb schema p.dbFile

  let tempProject = { p with dbFile = tempFile }

  let vs = shouldMigrate p

  match migrateDb tempProject with
  | [] -> nothingToMigrate vs
  | steps ->
    if not vs.shouldMigrate then
      Print.printYellow $"Have in mind since the project and database versions ({vs.projectVersion}) are the same,"
      Print.printYellow "the steps won't be executed. If you want to execute them,"
      Print.printYellow "please increase the project version in the file db.toml."
      Print.printYellow "Otherwise you can use the command `mig commit -a` to amend the last migration"
      printfn ""

    MigrationPrint.printMigrationIntent steps
