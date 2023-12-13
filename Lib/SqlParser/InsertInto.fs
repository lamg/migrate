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

module Migrate.SqlParser.InsertInto

open FParsec.Primitives
open Basic
open Types

module K = Keyword
module S = Symbol

let insertInto =

  parse {
    do! keyword K.Insert
    do! keyword K.Into
    let! tableName = ident
    let! columns = sepBy1 ident (symbol S.Comma) |> parens
    do! keyword K.Values

    let row = sepBy1 Scalar.literal (symbol S.Comma) |> parens
    let! values = sepBy1 row (symbol S.Comma)
    do! symbol S.Semicolon

    return
      { table = tableName
        columns = columns
        values = values }
  }
