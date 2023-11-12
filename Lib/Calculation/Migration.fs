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
open SqlParser.Types

let tablesMigration (dbSchema: SqlFile) (p: Project) =
  Solver.createTable dbSchema.tables p.source.tables

let viewsMigration (dbSchema: SqlFile) (p: Project) =
  Solver.createView dbSchema.views p.source.views

let zipHomologous (xs: 'a list) (ys: 'a list) (key: 'a -> string) (value: 'a -> 'b) =
  let keyValues = List.map (fun x -> key x, value x) >> Map.ofList
  let left = xs |> keyValues
  let right = ys |> keyValues
  let setLeft = left.Keys |> Set.ofSeq
  let setRight = right.Keys |> Set.ofSeq
  let common = Set.intersect setLeft setRight
  common |> Set.map (fun k -> k, left[k], right[k]) |> Set.toList

let findTable (schema: SqlFile) table =
  schema.tables |> List.find (fun t -> t.name = table)

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

let findPrimaryKey (t: CreateTable) =
  let colConstraint =
    t.columns
    |> List.mapi (fun i c -> i, c)
    |> List.tryFind (fun (_, c) ->
      c.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))

  match colConstraint with
  | Some(i, _) -> fun (xs: Expr list) -> [ xs[i] ]
  | None ->
    let kss =
      t.constraints
      |> List.choose (function
        | PrimaryKeyCols xs -> Some xs
        | _ -> None)

    match kss with
    | [] -> TableShouldHavePrimaryKey t.name |> raise
    | [ ks ] ->
      let keyIndexes =
        ks |> List.map (fun k -> t.columns |> List.findIndex (fun c -> c.name = k))

      fun (xs: Expr list) -> keyIndexes |> List.map (fun i -> xs.[i])
    | _ -> TableShouldHaveSinglePrimaryKey t.name |> raise


let insertsMigration (dbSchema: SqlFile) (p: Project) =
  let key (i: InsertInto) = i.table
  let value = id
  let homologousInserts = zipHomologous dbSchema.inserts p.source.inserts key value

  homologousInserts
  |> List.map (fun (_, left, right) ->
    let primaryKey = findTable p.source left.table |> findPrimaryKey

    Solver.insertInto primaryKey left right)
  |> List.concat

let migration (dbSchema: SqlFile) (p: Project) =
  let migrators =
    [ tablesMigration
      viewsMigration
      columnsMigration
      constraintsMigration
      insertsMigration ]

  let findMap (f: 'a -> 'b option) (xs: 'a list) = xs |> Seq.choose f |> Seq.tryHead

  let nonEmpty =
    function
    | [] -> None
    | xs -> Some xs

  let foundMigration migrator = migrator dbSchema p |> nonEmpty
  migrators |> findMap foundMigration
