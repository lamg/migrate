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

module internal Migrate.SqlGeneration.Column

open Migrate.Types
open Migrate.SqlParser
open Migrate.SqlGeneration.Table

let sqlAddColumn (table: string) (c: ColumnDef) =
  c.constraints
  |> List.exists (function
    | Default _ -> true
    | _ -> false)
  |> function
    | false -> NoDefaultValueForColumn $"{table}.{c.name}" |> raise
    | _ -> ()

  [ $"ALTER TABLE {table} ADD COLUMN {sqlColumnDef c}" ]

let sqlDropColumn (table: string) (c: ColumnDef) =
  [ $"ALTER TABLE {table} DROP COLUMN {c.name}" ]

let sqlUpdateColumn (views: CreateView list) (table: CreateTable) (left: ColumnDef) (right: ColumnDef) =
  if left.constraints <> right.constraints then
    sqlRecreateTable views table |> Some
  else
    None
