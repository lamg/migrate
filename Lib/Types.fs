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

module Migrate.Types

type SqlType =
  | SqlInteger
  | SqlText
  | SqlReal

type Autoincrement = Autoincrement

type Expr =
  | String of string
  | Integer of int
  | Real of double

type InsertInto =
  { table: string
    columns: string list
    values: Expr list list }

type ForeignKey =
  { columns: string list
    refTable: string
    refColumns: string list }

type ColumnConstraint =
  | PrimaryKey of string list
  | Autoincrement
  | NotNull
  | Unique of string list
  | Default of Expr
  | ForeignKey of ForeignKey

type ColumnDef =
  { name: string
    columnType: SqlType
    constraints: ColumnConstraint list }

type CreateView =
  { name: string
    selectUnion: SqlParser.Ast.Select }

type CreateTable =
  { name: string
    columns: ColumnDef list
    constraints: ColumnConstraint list }

type CreateIndex =
  { name: string
    table: string
    columns: string list }

type SqlFile =
  { tableSyncs: InsertInto list
    tableInits: InsertInto list
    views: CreateView list
    tables: CreateTable list
    indexes: CreateIndex list }

type TableSync = { table: string; idCol: int }

type Report = { src: string; dest: string }

type Project =
  { dbFile: string
    source: SqlFile
    syncs: string list
    inits: string list
    reports: Report list
    pullScript: string option
    schemaVersion: string
    versionRemarks: string }

type DbTomlFile =
  {
    /// <summary>
    /// List of environment variables whose values point to Sqlite databases
    /// </summary>
    dbFile: string

    /// <summary>
    /// List of SQL files defining the project
    /// </summary>
    files: string list

    /// <summary>
    /// List of table synchronizations
    /// </summary>
    syncs: string list

    /// <summary>
    /// List of tables to initialize with insert values if empty
    /// </summary>
    inits: string list
    /// <summary>
    /// List of reports (a view and a table that acts as cache for the data the view generates)
    /// </summary>
    reports: Report list

    /// <summary>
    /// Script to pull data from the production database
    /// </summary>
    pullScript: string option

    /// <summary>
    /// Schema version
    /// </summary>
    schemaVersion: string

    /// <summary>
    /// Remarks about the version
    /// </summary>
    versionRemarks: string
  }

type SqlStep = { sql: string; error: string option }

type Diff =
  | Added of string
  | Removed of string
  | Changed of string * string

type SolverProposal =
  { reason: Diff
    statements: string list }

type ProposalResult =
  { reason: Diff
    statements: string list
    error: string option }

type MigrationIntent =
  { versionRemarks: string
    steps: ProposalResult list
    schemaVersion: string
    date: string }

type Migration =
  { hash: string
    date: string
    dbFile: string
    versionRemarks: string
    sqlSteps: SqlStep list }

exception MalformedProject of string
exception ExpectingEnvVar of string
exception NoDefaultValueForColumn of string

type ColumnType =
  { table: string
    column: string
    sqlType: SqlType }

type ExprType = { expr: Expr; sqlType: SqlType }

type TableSyncSpec =
  { table: string
    tableCols: string list
    insertCols: string list }

exception NotMatchingTypes of (ExprType * ExprType)
exception ExpectingType of (SqlType * ExprType)
exception UnsupportedTypeInference of Expr
exception TableShouldHavePrimaryKey of string
exception TableShouldHaveSinglePrimaryKey of string
exception InvalidTableSync of TableSyncSpec

exception FailedLoadResFile of string
exception FailedOpenStore of string
exception CorruptedStore of string
type QueryError = { sql: string; error: string }
exception FailedQuery of QueryError
exception FailedParse of string

type OpenError = { dbFile: string; msg: string }
exception FailedOpenDb of OpenError
exception StaleMigration of ProposalResult list
