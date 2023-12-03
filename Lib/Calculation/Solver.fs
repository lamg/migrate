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

module internal Migrate.Calculation.Solver

open Migrate
open Migrate.Types
open SqlParser.Types
open Migrate.SqlGeneration

type SetResult<'a> =
  { left: Map<string, 'a>
    setLeft: Set<string>
    right: Map<string, 'a>
    setRight: Set<string> }

let listToSet (xs: 'a list) (ys: 'a list) (keySel: 'a -> string) =
  let pair x = keySel x, x
  let left = xs |> List.map pair |> Map.ofList
  let right = ys |> List.map pair |> Map.ofList
  let setLeft = left.Keys |> Set.ofSeq
  let setRight = right.Keys |> Set.ofSeq

  { left = left
    setLeft = setLeft
    right = right
    setRight = setRight }

let difference (r: 'a SetResult) =
  let left, setLeft, right, setRight = r.left, r.setLeft, r.right, r.setRight

  let removes = setLeft - setRight |> Set.toList |> List.map (fun d -> left[d])
  let adds = setRight - setLeft |> Set.toList |> List.map (fun d -> right[d])
  (removes, adds)

let intersect (r: 'a SetResult) =
  let left, setLeft, right, setRight = r.left, r.setLeft, r.right, r.setRight

  Set.intersect setLeft setRight
  |> Set.toList
  |> List.map (fun d -> left[d], right[d])

let createDelete
  (xs: 'a list)
  (ys: 'a list)
  (keySel: 'a -> string)
  (sqlDelete: 'a -> string list)
  (sqlCreate: 'a -> string list)
  =
  let sets = listToSet xs ys keySel
  let removes, adds = difference sets

  let drops: list<SolverProposal> =
    removes
    |> List.map (fun r ->
      { reason = Removed(keySel r)
        statements = sqlDelete r })

  let creates: list<SolverProposal> =
    adds
    |> List.map (fun r ->
      { reason = Added(keySel r)
        statements = sqlCreate r })

  drops @ creates

let createDeleteUpdate
  (xs: 'a list)
  (ys: 'a list)
  (toString: 'a -> string)
  (keySel: 'a -> string)
  (sqlDelete: 'a -> string list)
  (sqlCreate: 'a -> string list)
  (sqlUpdate: 'a -> 'a -> string list option)
  =
  let sets = listToSet xs ys keySel
  let removes, adds = difference sets
  let common = intersect sets

  let drops: list<SolverProposal> =
    removes
    |> List.map (fun r ->
      { reason = Removed(keySel r)
        statements = sqlDelete r })

  let creates: list<SolverProposal> =
    adds
    |> List.map (fun r ->
      { reason = Added(keySel r)
        statements = sqlCreate r })

  let renames: list<SolverProposal> =
    common
    |> List.choose (fun (x, y) ->
      sqlUpdate x y
      |> Option.map (fun xs ->
        { reason = Changed(toString x, toString y)
          statements = xs }))

  drops @ creates @ renames

let createTable (xs: CreateTable list) (ys: CreateTable list) =
  createDelete xs ys (_.name) Table.sqlDropTable Table.sqlCreateTable

let createView (xs: CreateView list) (ys: CreateView list) =
  createDelete xs ys (View.sqlCreateView >> Print.joinSqlPretty) View.sqlDropView View.sqlCreateView

let createIndex (xs: CreateIndex list) (ys: CreateIndex list) =
  createDelete xs ys (_.table) Index.sqlDropIndex Index.sqlCreateIndex

let columns (views: CreateView list) (table: CreateTable) (xs: ColumnDef list) (ys: ColumnDef list) =
  let keySel (x: ColumnDef) =
    $"{x.name} {Table.sqlColType x.``type``}"

  createDeleteUpdate
    xs
    ys
    Table.sqlColumnDef
    keySel
    (Column.sqlDropColumn table.name)
    (Column.sqlAddColumn table.name)
    (Column.sqlUpdateColumn views table)

let constraints (views: CreateView list) (right: CreateTable) (xs: ColumnConstraint list) (ys: ColumnConstraint list) =
  let keySel = Migrate.SqlGeneration.Table.sqlConstraint
  let constraintSolution _ = Table.sqlRecreateTable views right

  createDelete xs ys keySel constraintSolution constraintSolution

let insertInto (keyIndexes: int list) (left: InsertInto) (right: InsertInto) =
  let sqlExpr = Expr.sqlExpr (fun _ -> "")

  let keySel (xs: Expr list) =
    keyIndexes
    |> List.map (fun i -> xs[i])
    |> List.map sqlExpr
    |> String.concat ", "

  let toUpdate (leftRow: Expr list) (rightRow: Expr list) =
    if List.map sqlExpr leftRow <> List.map sqlExpr rightRow then
      Some(Row.sqlUpdateRow right keyIndexes rightRow)
    else
      None

  createDeleteUpdate
    left.values
    right.values
    keySel
    keySel
    (Row.sqlDeleteRow right keyIndexes)
    (Row.sqlInsertRow right)
    toUpdate
