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

module internal Migrate.Checks.RelationsSummary

open Migrate
open Types

let colsToString: ColumnType list -> string =
  List.map (fun c ->
    let colType =
      match c.sqlType with
      | Int -> "INTEGER"
      | Text -> "TEXT"
      | Bool -> "BOOLEAN"
      | Real -> "REAL"

    $"{c.column} {colType}")
  >> String.concat ", "

let formatRelations =
  TypeChecker.checkTypes
  >> List.groupBy (fun c -> c.table)
  >> List.map (fun (table, cols) -> $"{table} ({colsToString cols})")
  >> String.concat "\n"

let databaseRelations (p: Project) =
  DbProject.LoadDbSchema.dbSchema p |> formatRelations

let projectRelations (p: Project) =
  p.source |> formatRelations |> Print.colorizeSql
