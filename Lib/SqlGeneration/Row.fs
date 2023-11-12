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

open SqlParser.Types
open Util

let sqlExpr = Migrate.SqlGeneration.Expr.sqlExpr (fun _ -> "")

let rowToSetEqual (columns: string list) (row: Expr list) =
  List.zip row columns |> sepComma (fun (r, c) -> $"{c}={sqlExpr r}")

let sqlUpdateRow (i: InsertInto) (row: Expr list) =
  assert (row.Length >= 2 && i.columns.Length = row.Length)

  let idCol, restCols = i.columns.Head, i.columns.Tail
  let set = rowToSetEqual restCols row.Tail
  [ $"UPDATE {i.table} SET {set} WHERE {idCol}={sqlExpr row.Head}" ]

let sqlDeleteRow (i: InsertInto) (row: Expr list) =
  assert (row.Length >= 2 && i.columns.Length = row.Length)
  let idCol = i.columns.Head
  [ $"DELETE FROM {i.table} WHERE {idCol}={sqlExpr row.Head}" ]

let sqlInsertRow (i: InsertInto) (row: Expr list) =
  assert (i.columns.Length = row.Length)
  let values = row |> sepComma sqlExpr
  [ $"INSERT INTO {i.table}{i.columns} VALUES {values}" ]
