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

module internal Migrate.SqlGeneration.Row

open Migrate.Types
open Util

let sqlExpr =
  function
  | Integer v -> string v
  | String v -> $"'{v}'"
  | Real v -> string v

let rowToSetEqual (colValues: (string * Expr) list) =
  colValues |> sepComma (fun (c, v) -> $"{c} = {sqlExpr v}")

let rowToPred (colValues: (string * Expr) list) =
  colValues
  |> List.map (fun (c, v) -> $"{c} = {sqlExpr v}")
  |> String.concat " AND "

let sqlUpdateRow (ins: InsertInto) (keyIndexes: int list) (row: Expr list) =
  let nonKeyIndexes =
    [ 0 .. ins.columns.Length - 1 ]
    |> List.filter (fun i -> not (keyIndexes |> List.contains i))

  let keyCols = keyIndexes |> List.map (fun i -> ins.columns[i], row[i])
  let nonKeyCols = nonKeyIndexes |> List.map (fun i -> ins.columns[i], row[i])
  let set = rowToSetEqual nonKeyCols
  let rowMatch = rowToPred keyCols
  [ $"UPDATE {ins.table} SET {set} WHERE {rowMatch}" ]

let sqlDeleteRow (ins: InsertInto) (keyIndexes: int list) (row: Expr list) =
  let keyCols = keyIndexes |> List.map (fun i -> ins.columns[i], row[i]) |> rowToPred
  [ $"DELETE FROM {ins.table} WHERE {keyCols}" ]

let sqlInsertRow (i: InsertInto) (row: Expr list) =
  assert (i.columns.Length = row.Length)
  let values = row |> sepComma sqlExpr
  let cols = i.columns |> sepComma id
  [ $"INSERT INTO {i.table}({cols}) VALUES ({values})" ]
