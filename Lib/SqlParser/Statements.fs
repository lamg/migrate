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

module Migrate.SqlParser.Statements

open FParsec.Primitives
open Basic
open Migrate.SqlParser.Types


module K = Keyword
module S = Symbol

type Statement =
  | CreateTable of CreateTable
  | CreateView of CreateView
  | CreateIndex of CreateIndex
  | InsertInto of InsertInto

let statement =
  let table = CreateTable.createTable |>> CreateTable

  let view = CreateView.view |>> CreateView

  let index = CreateIndex.createIndex |>> CreateIndex

  let insert = InsertInto.insertInto |>> InsertInto

  let s = keyword K.Create >>. (table <|> view <|> index) <|> insert
  spaceComments >>. s

let statements =
  let emptyFile =
    { tables = []
      views = []
      indexes = []
      inserts = [] }

  many statement
  |>> List.fold
    (fun acc ->
      function
      | CreateTable t -> { acc with tables = t :: acc.tables }
      | CreateView v -> { acc with views = v :: acc.views }
      | CreateIndex i -> { acc with indexes = i :: acc.indexes }
      | InsertInto i -> { acc with inserts = i :: acc.inserts })
    emptyFile
  |>> fun r ->
    { tables = List.rev r.tables
      views = List.rev r.views
      indexes = List.rev r.indexes
      inserts = List.rev r.inserts }
