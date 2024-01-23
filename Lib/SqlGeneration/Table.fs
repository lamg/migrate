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

module internal Migrate.SqlGeneration.Table

open Migrate.Types
open Migrate.SqlGeneration.Util

let sqlConstraint =
  function
  | NotNull -> "NOT NULL"
  | PrimaryKey [] -> "PRIMARY KEY"
  | PrimaryKey xs -> $"PRIMARY KEY({sepComma id xs})"
  | Autoincrement -> "AUTOINCREMENT"
  | Default(String v) -> $"DEFAULT '{v}'"
  | Default(Integer v) -> $"DEFAULT {v}"
  | Default(Real v) -> $"DEFAULT {v}"
  | Unique [] -> "UNIQUE"
  | Unique xs -> $"UNIQUE({sepComma id xs})"
  | ForeignKey f ->
    let cols = f.columns |> sepComma id
    let refCols = f.refColumns |> sepComma id
    $"FOREIGN KEY({cols}) REFERENCES {f.refTable}({refCols})"

let sqlColType =
  function
  | SqlInteger -> "integer"
  | SqlText -> "text"
  | SqlReal -> "real"

let sqlColumnDef (c: ColumnDef) =
  let constraints = c.constraints |> List.map sqlConstraint |> String.concat " "
  $"{c.name} {c.columnType |> sqlColType} {constraints}"

let sqlTableConstraints (table: CreateTable) =
  match table.constraints with
  | [] -> ""
  | _ -> $", {table.constraints |> sepComma sqlConstraint}"

let sqlDropTable (table: CreateTable) = [ $"DROP TABLE {table.name}" ]

let sqlCreateTable (table: CreateTable) =
  let columns = table.columns |> sepComma sqlColumnDef
  let constraints = sqlTableConstraints table
  [ $"CREATE TABLE {table.name}({columns}{constraints})" ]

let sqlRenameTable (c: CreateTable) (n: CreateTable) =
  [ $"ALTER TABLE {c.name} RENAME TO {n.name}" ]

let dropDependentViews (views: CreateView list) (table: string) = []

let sqlRecreateTable (views: CreateView list) (table: CreateTable) =
  let auxTable =
    { table with
        name = $"{table.name}_aux" }

  let createAux = auxTable |> sqlCreateTable
  let auxColumns = auxTable.columns |> sepComma (fun c -> c.name)

  dropDependentViews views table.name
  @ createAux
  @ [ $"INSERT OR IGNORE INTO {auxTable.name}({auxColumns}) SELECT {auxColumns} FROM {table.name}"
      $"DROP TABLE {table.name}"
      $"ALTER TABLE {auxTable.name} RENAME TO {table.name}" ]
