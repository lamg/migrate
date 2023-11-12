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

module Checks

open Xunit
open SqlParser.Types
open Migrate.Checks.TypeChecker
open Migrate.Types

let view0 =
  { name = "view0"
    select =
      { withAliases = []
        select =
          { columns = [ Column { qualifier = None; ``member`` = "id" } ]
            distinct = false
            from =
              [ Table
                  { qualifier = None
                    ``member`` = "table0" } ]
            where = None
            groupBy = []
            orderBy = None
            having = None
            limit = None
            offset = None } } }

let table0 =
  { name = "table0"
    columns =
      [ { name = "id"
          ``type`` = SqlInteger
          constraints = [] } ]
    constraints = [] }

let emptyFile =
  { tables = []
    views = []
    indexes = []
    inserts = [] }

[<Fact>]
let fileWithTablesTest () =
  let f =
    { emptyFile with
        tables = [ table0 ]
        views = [ view0 ] }

  let r = checkTypes f

  let expected =
    [ { table = "table0"
        column = "id"
        sqlType = Int }
      { table = "view0"
        column = "id"
        sqlType = Int } ]

  Assert.Equal<ColumnType list>(expected, r)

[<Fact>]
let viewWithAbsentTable () =
  let f = { emptyFile with views = [ view0 ] }

  try
    checkTypes f |> ignore
  with UndefinedIdentifier msg ->
    Assert.Equal(msg, { qualifier = None; ``member`` = "id" })

[<Fact>]
let exprTypeNoAlias () =
  let var = { qualifier = None; ``member`` = "id" }

  let colTypes =
    [ { table = "table0"
        column = "id"
        sqlType = Int } ]

  let fromExpr =
    [ Table
        { qualifier = None
          ``member`` = "table0" } ]

  let r = inferVarType colTypes fromExpr var
  Assert.Equal(Int, r)

[<Fact>]
let exprTypeAlias () =
  let var =
    { qualifier = Some "t"
      ``member`` = "id" }

  let colTypes =
    [ { table = "table0"
        column = "id"
        sqlType = Int } ]

  let fromExpr =
    [ Alias
        { alias = "t"
          expr =
            Table
              { qualifier = None
                ``member`` = "table0" } }

      ]

  let r = inferVarType colTypes fromExpr var
  Assert.Equal(Int, r)

[<Fact>]
let funcType () =
  let exprs =
    [ Func
        { name = "date"
          args = []
          window = None },
      Text
      Func
        { name = "coalesce"
          args = [ String "0"; String "0" ]
          window = None },
      Text ]

  exprs |> List.iter (fun (e, t) -> Assert.Equal(inferType [] [] e, t))

[<Fact>]
let topologicalSort () =
  let references =
    function
    | 0 -> [ 1; 2; 3 ]
    | 2 -> [ 4; 5 ]
    | 4 -> [ 6 ]
    | _ -> []

  let xs = [ 0..10 ] |> Migrate.Checks.Algorithms.topologicalSort references

  let expected = [ 10..-1..0 ]
  Assert.Equal<int list>(expected, xs)
