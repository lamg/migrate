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

module internal Migrate.Checks.Types

open System.Collections.Generic
open Migrate
open Migrate.Types
open Types

type Record = Map<string, SqlType>

let createTableType (ct: CreateTable) : Record =
  ct.columns |> List.map (fun c -> c.name, c.columnType) |> Map.ofList

let projectionType (t: Record) (projection: string list) : Record option =
  try
    projection |> List.map (fun col -> col, t[col]) |> Map.ofList |> Some
  with :? KeyNotFoundException ->
    None

let implicitJoinType (joined: Map<string, Record>) (projection: (string * string) list) : Record option =
  try
    projection
    |> List.map (fun (qualifier, column) -> column, joined[qualifier][column])
    |> Map.ofList
    |> Some
  with :? KeyNotFoundException ->
    None


// let colsToString: ColumnType list -> string =
//   List.map (fun c ->
//     let colType =
//       match c.sqlType with
//       | Int -> "INTEGER"
//       | Text -> "TEXT"
//       | Bool -> "BOOLEAN"
//       | Real -> "REAL"
//
//     $"{c.column} {colType}")
//   >> String.concat ", "

let formatRelations = failwith "not implemented"
// Checks.TypeChecker.checkTypes
// >> List.groupBy _.table
// >> List.map (fun (table, cols) -> $"{table} ({colsToString cols})")
// >> String.concat "\n"

let databaseRelations (p: Project) =
  use conn = DbUtil.openConn p.dbFile
  DbProject.LoadDbSchema.dbSchema p conn |> formatRelations

let projectRelations (p: Project) =
  p.source |> formatRelations |> DbUtil.colorizeSql
