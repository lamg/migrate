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

module internal Migrate.Calculation.Migration

open Migrate.Types
open TableSync

let tablesMigration (dbSchema: SqlFile) (p: Project) =
  Solver.createTable dbSchema.tables p.source.tables

let viewsMigration (dbSchema: SqlFile) (p: Project) =
  Solver.createView dbSchema.views p.source.views

let columnsMigration (dbSchema: SqlFile) (p: Project) =
  let homologousColumns =
    zipHomologous dbSchema.tables p.source.tables (fun c -> c.name) (fun c -> c.columns)

  homologousColumns
  |> List.map (fun (table, left, right) -> Solver.columns dbSchema.views (findTable p.source table) left right)
  |> List.concat

let constraintsMigration (dbSchema: SqlFile) (p: Project) =
  let homologousConstraints =
    zipHomologous dbSchema.tables p.source.tables (fun c -> c.name) (fun c -> c.constraints)

  homologousConstraints
  |> List.map (fun (table, left, right) -> Solver.constraints dbSchema.views (findTable p.source table) left right)
  |> List.concat

let tableInitsMigration (dbSchema: SqlFile) (p: Project) =
  let filterInits =
    List.filter (fun (t: InsertInto) -> p.inits |> List.exists (fun x -> x = t.table))

  let leftInits = dbSchema.tableInits |> filterInits
  let rightInits = p.source.tableInits |> filterInits
  let homologousInits = zipHomologous leftInits rightInits (_.table) id

  homologousInits
  |> List.map (fun (_, left, right) -> Solver.tableInits left right)
  |> List.concat

let migration (dbSchema: SqlFile) (p: Project) =
  let migrators =
    [ tablesMigration
      viewsMigration
      columnsMigration
      constraintsMigration
      tableSyncsMigration
      tableInitsMigration ]

  let findMap (f: 'a -> 'b option) (xs: 'a list) = xs |> Seq.choose f |> Seq.tryHead

  let nonEmpty =
    function
    | [] -> None
    | xs -> Some xs

  let foundMigration migrator = migrator dbSchema p |> nonEmpty
  migrators |> findMap foundMigration
