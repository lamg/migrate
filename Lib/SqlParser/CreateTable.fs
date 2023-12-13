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

module Migrate.SqlParser.CreateTable

open FParsec.Primitives
open Basic
open Migrate.SqlParser.Types

module K = Keyword
module S = Symbol

let parseDefault =
  parse {
    do! keyword K.Default
    let! value = Scalar.literal
    return (Default value)
  }

let unique =
  parse {
    do! keyword K.Unique
    let! cols = parens (sepBy1 ident (symbol S.Comma))
    return Choice1Of2(Unique cols)
  }

let primaryKeyCols =
  parse {
    do! keyword K.Primary
    do! keyword K.Key
    let! cols = parens (sepBy1 ident (symbol S.Comma))

    return Choice1Of2(PrimaryKeyCols cols)
  }

let primaryKey =
  parse {
    do! keyword K.Primary
    do! keyword K.Key
    let! autoInc = opt (keyword K.Autoincrement >>. preturn Autoincrement)

    return PrimaryKey autoInc
  }

let foreignKey =
  parse {
    do! keyword K.Foreign
    do! keyword K.Key
    let! cols = parens (sepBy1 ident (symbol S.Comma))
    do! keyword K.References
    let! refTable = ident
    let! refCols = parens (sepBy1 ident (symbol S.Comma))

    return
      Choice1Of2(
        ForeignKey
          { columns = cols
            refTable = refTable
            refColumns = refCols }
      )
  }

let colOrConstraint: Parser<Choice<ColumnConstraint, ColumnDef>, unit> =
  unique
  <|> primaryKeyCols
  <|> foreignKey
  <|> parse {
    let! name = ident

    let! ``type`` =
      keyword K.Integer >>. preturn SqlInteger
      <|> (keyword K.Text >>. preturn SqlText)

    let! constraints =
      keyword K.Not >>. keyword K.Null >>. preturn NotNull
      <|> primaryKey
      <|> parseDefault
      |> many

    return
      Choice2Of2
        { name = name
          ``type`` = ``type``
          constraints = constraints }
  }

let partitionMap f xs =
  let rec loop (acc0, acc1) =
    function
    | [] -> (acc0 |> List.rev, acc1 |> List.rev)
    | x :: xs ->
      match f x with
      | Choice1Of2 y -> loop (y :: acc0, acc1) xs
      | Choice2Of2 y -> loop (acc0, y :: acc1) xs

  loop ([], []) xs

let createTable: Parser<CreateTable, unit> =

  parse {
    do! keyword K.Table
    let! table = ident
    let! colsAndConstraints = sepBy1 colOrConstraint (symbol S.Comma) |> parens
    do! symbol S.Semicolon

    let constraints, cols = colsAndConstraints |> partitionMap id

    return
      { name = table
        columns = cols
        constraints = constraints }
  }
