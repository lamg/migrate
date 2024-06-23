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

module internal Migrate.Reports.RelationSummary

open Migrate
open Types

let colsToString (xs: list<Checks.Types.ColumnId * SqlType>) =
  xs
  |> List.map (fun (col, t) ->
    let colType =
      match t with
      | SqlInteger -> "INTEGER"
      | SqlText -> "TEXT"
      | SqlReal -> "REAL"

    $"{col.name} {colType}")
  |> String.concat ", "

let formatRelations s =
  let types, errs = Checks.Types.typeCheck s

  match errs with
  | [] ->
    types
    |> Map.toList
    |> List.groupBy (fun (k, _) -> k.table)
    |> List.map (fun (table, cols) -> $"{table} ({colsToString cols})")
    |> String.concat "\n"
    |> DbUtil.colorizeSql
    |> printfn "%s"
  | _ ->
    let e = errs |> String.concat "\n"
    eprintfn $"type check errors:\n{e}"

let databaseRelations (p: Project) =
  use conn = DbUtil.openConn p.dbFile
  DbProject.LoadDbSchema.dbSchema p conn |> formatRelations

let projectRelations (p: Project) = p.source |> formatRelations
