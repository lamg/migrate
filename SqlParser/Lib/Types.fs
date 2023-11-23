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

module SqlParser.Types

type Var =
  { qualifier: string option
    ``member``: string }

type BinaryOp = { left: Expr; right: Expr }

and Alias = { expr: Expr; alias: string }

and OrderBy = { columns: Var list; asc: bool }

and Select =
  { columns: Expr list
    distinct: bool
    from: Expr list
    where: Expr option
    groupBy: Var list
    orderBy: OrderBy option
    having: Expr option
    limit: int option
    offset: int option }

and WithAlias = { alias: string; select: Select }

and WithSelect =
  { withAliases: WithAlias list
    select: Select }

and CaseWhen =
  { cond: Expr
    thenExpr: Expr
    elseExpr: Expr }

and Window =
  { partitionBy: Var list
    orderBy: OrderBy option }

and Func =
  { name: string
    args: Expr list
    window: Window option }

and InExpr = { tuple: Var list; select: WithSelect }

and JoinOn = { relation: Expr; onExpr: Expr }

and FromExpr = Expr list
and WhereExpr = Expr option

and Expr =
  | Integer of int
  | String of string
  | Row of Expr list
  | CaseWhen of CaseWhen
  | Func of Func
  | Column of Var
  | And of BinaryOp
  | Or of BinaryOp
  | Eq of BinaryOp
  | Neq of BinaryOp
  | Gt of BinaryOp
  | Gte of BinaryOp
  | Lt of BinaryOp
  | Lte of BinaryOp
  | Concat of BinaryOp
  | Not of Expr
  | Like of BinaryOp
  | Exists of Expr
  | In of BinaryOp
  | SubQuery of WithSelect
  | Table of Var
  | Alias of Alias
  | InnerJoin of BinaryOp
  | LeftOuterJoin of BinaryOp
  | JoinOn of JoinOn
  | EnvVar of Var

type CreateView = { name: string; select: WithSelect }

type InsertInto =
  { table: string
    columns: string list
    values: Expr list list }

type SqlType =
  | SqlInteger
  | SqlText

type Autoincrement = Autoincrement

type ForeignKey =
  { columns: string list
    refTable: string
    refColumns: string list }

type ColumnConstraint =
  | PrimaryKey of Autoincrement option
  | PrimaryKeyCols of string list
  | NotNull
  | Unique of string list
  | Default of Expr
  | ForeignKey of ForeignKey

type ColumnDef =
  { name: string
    ``type``: SqlType
    constraints: ColumnConstraint list }

type CreateTable =
  { name: string
    columns: ColumnDef list
    constraints: ColumnConstraint list }

type CreateIndex =
  { name: string
    table: string
    column: string }

type SqlFile =
  { inserts: InsertInto list
    views: CreateView list
    tables: CreateTable list
    indexes: CreateIndex list }

type ParseError =
  { position: int * int
    element: string
    formatted: string }
