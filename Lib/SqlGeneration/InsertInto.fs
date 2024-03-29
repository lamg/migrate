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

module internal Migrate.SqlGeneration.InsertInto

open Migrate.Types

let sqlLiteral (e: Expr) =
  match e with
  | Integer c -> $"{c}"
  | String s -> $"'{s}'"
  | Real r -> $"{r}"

let sqlRowToString (vs: Expr list) =
  vs |> List.map sqlLiteral |> String.concat ", " |> (fun v -> $"({v})")

let sqlColumnNames (i: InsertInto) =
  i.columns |> String.concat ", " |> (fun c -> $"({c})")

let sqlValues (vss: Expr list list) =
  vss |> List.map sqlRowToString |> String.concat ",\n"

let sqlInsertInto (i: InsertInto) =
  match i.values with
  | [] -> []
  | _ ->
    let columns = sqlColumnNames i
    let values = i.values |> sqlValues
    [ $"INSERT INTO {i.table}{columns} VALUES\n{values}" ]
