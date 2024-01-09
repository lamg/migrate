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

module Migrate.Cli

open System.IO
open Migrate.Reports
open Migrate.Types
open Migrate.Execution

let openConn = DbUtil.openConn

/// <summary>
/// Executes a migration
/// </summary>
let commit p =
  try
    Commit.migrateAndCommit p
    0
  with
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedParse e ->
    Print.printRed e
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1
  | FailedOpenStore e ->
    Print.printRed e
    1
  | StaleMigration xs ->
    Print.printRed $"Stale migration {xs}"
    1
  | ExpectingEnvVar x ->
    Print.printError $"Expecting environment variable {x}"
    1

/// <summary>
/// Executes a migration
/// </summary>
let commitAmend p =
  try
    Commit.commitAmend p
    0
  with
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedParse e ->
    Print.printRed e
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1
  | FailedOpenStore e ->
    Print.printRed e
    1
  | StaleMigration xs ->
    Print.printRed $"Stale migration {xs}"
    1
  | ExpectingEnvVar x ->
    Print.printError $"Expecting environment variable {x}"
    1


/// <summary>
/// Performs a manual migration. Fails if the resulting database schema
/// differs from the one in the source files.
/// </summary>
let manualMigration p =
  try
    Commit.manualMigration p
    0
  with
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedParse e ->
    Print.printRed e
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1
  | FailedOpenStore e ->
    Print.printRed e
    1
  | StaleMigration xs ->
    Print.printRed $"Stale migration {xs}"
    1
  | ExpectingEnvVar x ->
    Print.printError $"Expecting environment variable {x}"
    1

/// <summary>
/// Shows the calculated steps to transform the database into the
/// desired project schema
/// </summary>
let status p =
  try
    Commit.dryMigration p
    0
  with
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedOpenStore e ->
    Print.printRed e
    1
  | FailedParse e ->
    Print.printRed e
    1
  | TableShouldHavePrimaryKey t ->
    $"Table {t} should have a primary key. "
    + "This happens for synchronized tables where the CREATE TABLE statement doesn't declare a PRIMARY KEY"
    |> Print.printRed

    1
  | TableShouldHaveSinglePrimaryKey t ->
    $"Table {t} has two or more PRIMARY KEY declarations" |> Print.printRed
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql} \n got error: {e.error}"

    1
  | StaleMigration xs ->
    Print.printRed $"Stale migration {xs}"
    1
  | ExpectingEnvVar x ->
    Print.printError $"Expecting environment variable {x}"
    1

/// <summary>
/// Shows the current database schema
/// </summary>
let dumpDbSchema (p: Project) =
  use conn = openConn p.dbFile
  DbProject.LoadDbSchema.rawDbSchema conn |> DbUtil.colorizeSql

let dumpDbSchemaNoColor (p: Project) =
  use conn = openConn p.dbFile
  DbProject.LoadDbSchema.rawDbSchema conn

/// <summary>
/// Shows the detailed steps of the last migration
/// </summary>
let lastLogDetailed (p: Project) =
  try
    use conn = DbUtil.openConn p.dbFile

    Execution.Store.Get.getMigrations conn
    |> List.tryHead
    |> Option.iter Store.Print.printLog

    0
  with
  | FailedOpenStore e ->
    $"Failed opening migrations store: {e}" |> Print.printRed
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1

/// <summary>
/// Shows the summarized steps of the last migration
/// </summary>
let lastLogShort (p: Project) =
  try
    use conn = DbUtil.openConn p.dbFile

    Store.Get.getMigrations conn
    |> List.tryLast
    |> Option.iter Store.Print.printShortLog

    0
  with
  | FailedOpenStore e ->
    $"Failed opening migrations store: {e}" |> Print.printRed
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1

/// <summary>
/// Shows the migration log
/// </summary>
let log p =
  try
    Store.Print.showMigrations p
    0
  with
  | FailedOpenStore e ->
    $"Failed opening migrations store: {e}" |> Print.printRed
    1
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1

/// <summary>
/// Shows the steps in a migration
/// </summary>
/// <param name="p">Project</param>
/// <param name="commitHash">Commit hash</param>
let logDetailed (p: Project) (commitHash: string) =
  try
    use conn = DbUtil.openConn p.dbFile

    Store.Get.getMigrations conn
    |> List.tryFind (fun m -> m.migration.hash.Contains commitHash)
    |> Option.iter Store.Print.printLog

    0
  with
  | FailedOpenStore e ->
    $"Failed opening migrations store: {e}" |> Print.printRed
    1
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1

/// <summary>
/// Shows the reports
/// </summary>
/// <param name="p">Project</param>
let showReports p =
  try
    Report.showReports p
    0
  with FailedOpenDb e ->
    $"No database found:\n{e}" |> Print.printRed
    1

/// <summary>
/// Synchronizes reports (views and tables used as caches for their results)
/// </summary>
let syncReports p =
  try
    Report.syncReports p
    0
  with FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1

/// <summary>
/// Convenience function for loading project files from assembly resources
/// Raises MalformedProject in case of failure
/// </summary>
let loadResourceFile (asm: System.Reflection.Assembly) (prefix: string) (file: string) =
  Migrate.DbProject.LoadProjectFiles.loadResourceFile asm prefix file

/// <summary>
/// Loads a project using a custom file reader
/// </summary>
let loadProjectWith (loadFile: string -> string) =
  Migrate.DbProject.LoadProjectFiles.loadProjectWith loadFile

/// <summary>
/// Loads a project from a directory if specified or the current one instead
/// </summary>
let loadProjectFromDir (dir: string option) =
  Migrate.DbProject.LoadProjectFiles.loadProjectFromDir dir

/// <summary>
/// Initializes a project in the current directory
/// </summary>
let initProject () =
  try
    DbProject.InitProject.initProject ()
    0
  with
  | MalformedProject e ->
    $"Malformed project: {e}" |> Print.printRed
    1
  | :? FileNotFoundException as e ->
    $"File not found: {e}" |> Print.printRed
    1

let printDbRelations (p: Project) =
  try
    let summary = RelationsSummary.databaseRelations p
    Print.printYellow "relations"
    printfn $"{summary}"
    0
  with
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedParse e ->
    Print.printRed e
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1
  | MalformedProject e ->
    $"Malformed project: {e}" |> Print.printRed
    1

let printProjectRelations (p: Project) =
  try
    let summary = RelationsSummary.projectRelations p
    Print.printYellow "relations"
    printfn $"{summary}"
    0
  with
  | UnsupportedTypeInference e ->
    Print.printRed $"unsupported type inference {e}"
    1
  | FailedOpenDb e ->
    $"Failed to open database {e.dbFile}: {e.msg}" |> Print.printRed
    1
  | FailedParse e ->
    Print.printRed e
    1
  | FailedQuery e ->
    Print.printRed $"executing: {e.sql}\ngot error: {e.error}"
    1
  | MalformedProject e ->
    $"Malformed project: {e}" |> Print.printRed
    1

let exportRelation (p: Project) (relation: string) =
  match Export.exportRelation p relation with
  | Some sql ->
    printfn $"{sql}"
    0
  | None ->
    Print.printError $"relation {relation} not found"
    1
