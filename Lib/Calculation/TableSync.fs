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

module internal Migrate.Calculation.TableSync

open Migrate.Types
open SqlParser.Types

let findTable (schema: SqlFile) table =
  schema.tables |> List.find (fun t -> t.name = table)

let zipHomologous (xs: 'a list) (ys: 'a list) (key: 'a -> string) (value: 'a -> 'b) =
  let keyValues = List.map (fun x -> key x, value x) >> Map.ofList
  let left = xs |> keyValues
  let right = ys |> keyValues
  let setLeft = left.Keys |> Set.ofSeq
  let setRight = right.Keys |> Set.ofSeq
  let common = Set.intersect setLeft setRight
  common |> Set.map (fun k -> k, left[k], right[k]) |> Set.toList

let findKeyCols (t: CreateTable) =
  let colKey =
    t.columns
    |> List.tryFind (fun c ->
      c.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))

  match colKey with
  | Some col ->
    // the table has a single column primary key
    [ col.name ]
  | None ->
    let kss =
      t.constraints
      |> List.choose (function
        | PrimaryKeyCols xs -> Some xs
        | _ -> None)

    match kss with
    | [] -> TableShouldHavePrimaryKey t.name |> raise
    | [ ks ] ->
      // the table has several columns as primary key
      ks
    | _ -> TableShouldHaveSinglePrimaryKey t.name |> raise

let findKeyIndexes (c: CreateTable) (keyCols: string list) =
  keyCols
  |> List.map (fun col -> c.columns |> List.findIndex (fun c -> c.name = col))

let toSwaps (xs: string list) (ys: string list) =
  let xs = xs |> Array.ofList
  let ys = ys |> Array.ofList
  let swaps = Array.create xs.Length -1

  for i = 0 to xs.Length - 1 do
    let j = ys |> Array.findIndex (fun y -> y = xs[i])
    swaps[i] <- j

  swaps

let reorderList (swaps: int array) (xs: 'a list) =
  let ys = Array.create xs.Length xs.Head

  for i = 0 to xs.Length - 1 do
    ys[swaps[i]] <- xs[i]

  ys |> Array.toList

let insertsMigration (dbSchema: SqlFile) (p: Project) =
  let key (i: InsertInto) = i.table
  let value = id
  let homologousInserts = zipHomologous dbSchema.inserts p.source.inserts key value

  homologousInserts
  |> List.map (fun (_, left, right) ->
    let table = findTable p.source left.table
    let cols = table.columns |> List.map (fun c -> c.name)
    let leftSwaps = toSwaps cols left.columns
    let rightSwaps = toSwaps cols right.columns

    let left =
      { table = left.table
        columns = reorderList leftSwaps left.columns
        values = left.values |> List.map (reorderList leftSwaps) }

    let right =
      { table = right.table
        columns = reorderList rightSwaps right.columns
        values = right.values |> List.map (reorderList rightSwaps) }

    let primaryKey = table |> findKeyCols |> findKeyIndexes table

    Solver.insertInto primaryKey left right)
  |> List.concat
