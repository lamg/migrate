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

let openConn = DbUtil.openConn

/// <summary>
/// Executes a migration
/// </summary>
let commit p =
  try
    Execution.Commit.migrateAndCommit p
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
    Execution.Commit.manualMigration p
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
    Execution.Commit.dryMigration p
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
  DbProject.LoadDbSchema.rawDbSchema p |> Print.colorizeSql

/// <summary>
/// Shows the detailed steps of the last migration
/// </summary>
let lastLogDetailed p =
  try
    MigrationStore.getMigrations p
    |> List.tryLast
    |> Option.iter MigrationPrint.printLog

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
let lastLogShort p =
  try
    MigrationStore.getMigrations p
    |> List.tryLast
    |> Option.iter MigrationPrint.printShortLog

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
    MigrationPrint.showMigrations p
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
let logDetailed p (commitHash: string) =
  try
    MigrationStore.getMigrations p
    |> List.tryFind (fun m -> m.migration.hash.Contains commitHash)
    |> Option.iter MigrationPrint.printLog

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
/// Loads a project from the file system
/// Raises MalformedProject in case of failure
/// </summary>
let loadProject = Migrate.DbProject.LoadProjectFiles.loadProject
let loadProjectFromRes = Migrate.DbProject.LoadProjectFiles.loadProjectFromRes

/// <summary>
/// Loads a file from the resources embedded in the assembly
/// Raises FailedLoadingResFile in case of failure
/// </summary>
let loadFromRes = Print.loadFromRes

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
  | UndefinedIdentifier e ->
    Print.printRed $"undefined identifier {e}"
    1
  | UnsupportedTypeInference e ->
    Print.printRed $"unsupported type inference {e}"
    1
  | DuplicatedDefinition e ->
    Print.printRed $"duplicated definition {e}"
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