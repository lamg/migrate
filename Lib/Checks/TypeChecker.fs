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

module internal Migrate.Checks.TypeChecker

open Migrate
open Types
open SqlParser.Types

let inferVarType (cols: ColumnType list) (fromExpr: Expr list) (v: Var) =
  let findColType (table: string) (col: string) =
    let table =
      fromExpr
      |> List.choose (function
        | Alias { expr = Table x; alias = a } when a = table -> Some x.``member``
        | _ -> None)
      |> List.tryHead
      |> function
        | Some t -> t
        | None -> table

    cols
    |> List.choose (function
      | c when c.table = table && c.column = col -> Some c.sqlType
      | _ -> None)
    |> function
      | [ x ] -> x
      | [] -> UndefinedIdentifier v |> raise
      | _ -> DuplicatedDefinition v |> raise

  match v with
  | { qualifier = Some t; ``member`` = x } -> findColType t x
  | { qualifier = None; ``member`` = x } ->
    match fromExpr with
    | [ Table { qualifier = None; ``member`` = n } ] -> findColType n x
    | _ -> CannotInferTypeWithoutTable v |> raise

/// <summary>
/// Checks query types and references
/// </summary>
let rec inferType (cols: ColumnType list) (fromExpr: Expr list) (s: Expr) =
  let boolOp (x: Expr) (y: Expr) =
    match inferType cols fromExpr x, inferType cols fromExpr y with
    | a, b when a = b -> Bool
    | a, b -> NotMatchingTypes({ expr = x; sqlType = a }, { expr = y; sqlType = b }) |> raise

  match s with
  | And { left = x; right = y } -> boolOp x y
  | Or { left = x; right = y } -> boolOp x y
  | Not x ->
    match inferType cols fromExpr x with
    | Bool -> Bool
    | r -> ExpectingType(Bool, { expr = x; sqlType = r }) |> raise
  | Eq { left = x; right = y } -> boolOp x y
  | Neq { left = x; right = y } -> boolOp x y
  | Gt { left = x; right = y } -> boolOp x y
  | Gte { left = x; right = y } -> boolOp x y
  | Lt { left = x; right = y } -> boolOp x y
  | Lte { left = x; right = y } -> boolOp x y
  | In { left = x; right = y } -> boolOp x y
  | Integer _ -> Int
  | String _ -> Text
  | Column c -> inferVarType cols fromExpr c
  | Func { name = n } when n.ToLower() = "date" -> Text
  | Func { name = n; args = [ _; p ] } when n.ToLower() = "coalesce" -> inferType cols fromExpr p
  | Func { name = n } when n.ToLower() = "strftime" -> Text
  | Func { name = n } when n.ToLower() = "sum" -> Int
  | Func { name = n } when n.ToLower() = "count" -> Int
  | Func { name = n } when n.ToLower() = "row_number" -> Int
  | Alias { expr = e } -> inferType cols fromExpr e
  | s -> UnsupportedTypeInference s |> raise

// creates a ColumnType list from the column types of each table
let baseTypes (xs: CreateTable list) =
  xs
  |> List.map (fun x ->
    x.columns
    |> List.map (fun c ->
      { table = x.name
        column = c.name
        sqlType =
          c.``type``
          |> function
            | SqlInteger -> Int
            | SqlText -> Text }))
  |> List.concat

let selectSignature (xs: ColumnType list) (s: Select) =
  s.columns |> List.map (inferType xs s.from)

let generalSelectSignature (xs: ColumnType list) (g: WithSelect) =
  // ys is the result of calculating the types for each with clause
  // and representing the result as if it were the types of a regular table
  let ys =
    g.withAliases
    |> List.map (fun x ->
      selectSignature xs x.select
      |> List.zip x.select.columns
      |> List.map (fun (e, ``type``) ->
        { table = x.alias
          column =
            match e with
            | Alias a -> a.alias
            | Column v -> v.``member``
            | e -> failwith $"invalid expression {e} in select"
          sqlType = ``type`` }))
    |> List.concat

  selectSignature (xs @ ys) g.select

let selectReferences (v: Select) =
  v.from
  |> List.map (function
    | Table t -> t.``member``
    | Alias { expr = Table t } -> t.``member``
    | f -> failwith $"not supported from expression {f}")

let withSelectReferences (v: WithSelect) =
  let ws =
    v.withAliases |> List.map (fun s -> selectReferences s.select) |> List.concat

  ws @ selectReferences v.select

let viewReferences (xs: CreateView list) (x: CreateView) =
  x.selectUnion
  |> List.map (fun s ->
    s
    |> withSelectReferences
    |> List.choose (fun v -> xs |> List.tryFind (fun u -> u.name = v)))
  |> List.concat

let checkTypes (f: SqlFile) =
  let xs = baseTypes f.tables

  f.views
  |> Algorithms.topologicalSort (viewReferences f.views)
  |> List.fold
    (fun acc v ->
      let select = v.selectUnion.Head

      let acc' =
        generalSelectSignature acc select
        |> List.zip select.select.columns
        |> List.map (fun (e, ``type``) ->
          { table = v.name
            column =
              match e with
              | Alias a -> a.alias
              | Column v -> v.``member``
              | e -> failwith $"invalid expression {e} in select"
            sqlType = ``type`` })

      acc @ acc')
    xs
