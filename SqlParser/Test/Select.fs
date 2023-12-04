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

module Select

open Xunit
open SqlParser.Types
open Util
open FParsec.Primitives

module K = SqlParser.Keyword
module S = SqlParser.Symbol

[<Fact>]
let alias () =
  let cases =
    [ "table0", table "table0"
      "table0 as t0", tableAlias "table0" "t0"
      "table0 t0", tableAlias "table0" "t0" ]

  let p = SqlParser.Select.aliasTable pzero
  let testName = "Alias"
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" p)

[<Fact>]
let joinOp () =
  let operators =
    { left = tableAlias "table0" "t0"
      right = tableAlias "table1" "t1" }

  let inner = InnerJoin operators
  let outer = LeftOuterJoin operators

  let cases =
    [ "table0", table "table0"
      "table0 as t0", tableAlias "table0" "t0"
      "table0 t0", tableAlias "table0" "t0"
      "table0 t0 JOIN table1 t1", inner
      "table0 t0 INNER JOIN table1 t1", inner
      "table0 t0 LEFT OUTER JOIN table1 t1", outer ]

  let p = SqlParser.Select.joinOp pzero
  let testName = "JoinOp"
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" p)

[<Fact>]
let tableExpr () =
  let operators =
    { left = tableAlias "table0" "t0"
      right = tableAlias "table1" "t1" }

  let inner = InnerJoin operators
  let outer = LeftOuterJoin operators
  let onExpr = eq (column "t0" "id") (column "t1" "id")

  let cases =
    [ "table0 t0 JOIN table1 t1 ON t0.id = t1.id", JoinOn { relation = inner; onExpr = onExpr }
      "table0 t0 INNER JOIN table1 t1 ON t0.id = t1.id", JoinOn { relation = inner; onExpr = onExpr }

      "table0 t0 LEFT OUTER JOIN table1 t1 ON t0.id = t1.id", JoinOn { relation = outer; onExpr = onExpr } ]

  let p = SqlParser.Select.tableExpr pzero
  let testName = "TableExpr"
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" p)

[<Fact>]
let fromExpr () =
  let cases = [ "table0, table1", ([ table "table0"; table "table1" ], S.Semicolon) ]

  let testName = "FromExpr"
  let p = SqlParser.Select.fromExpr pzero
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" p)

let empty =
  { columns = []
    distinct = false
    from = []
    where = None
    groupBy = []
    having = None
    orderBy = None
    limit = None
    offset = None }

[<Fact>]
let selectExpr () =

  let selectWhere =
    { empty with
        from = [ table "table0" ]
        where = Some(Gt { left = col "a"; right = col "b" }) }

  let cases =
    [ "SELECT * FROM table0 WHERE a > b;", selectWhere
      "SELECT * FROM table0 WHERE a > b", selectWhere
      "SELECT * FROM table0 t0",
      { empty with
          from = [ tableAlias "table0" "t0" ] }
      "SELECT * FROM table0 t0 WHERE a > b",
      { selectWhere with
          from = [ tableAlias "table0" "t0" ] }
      "SELECT a FROM table0",
      { empty with
          columns = [ col "a" ]
          from = [ table "table0" ] }
      "SELECT a.b AS c FROM table0",
      { empty with
          columns = [ columnAlias "a" "b" "c" ]
          from = [ table "table0" ] }
      "SELECT * FROM t0 GROUP BY x.a ,y.b",
      { empty with
          from = [ table "t0" ]
          groupBy = [ var "x" "a"; var "y" "b" ] }
      "SELECT * FROM t0 ORDER BY x.a, y.b",
      { empty with
          from = [ table "t0" ]
          orderBy =
            Some
              { columns = [ var "x" "a"; var "y" "b" ]
                asc = true } }
      "SELECT date('now')",
      { empty with
          columns =
            [ Func
                { name = "date"
                  args = [ String "now" ]
                  window = None } ] }
      "SELECT * FROM t0 GROUP BY t0.c0 ORDER BY t0.id",
      { empty with
          from = [ table "t0" ]
          groupBy = [ var "t0" "c0" ]
          orderBy =
            Some
              { columns = [ var "t0" "id" ]
                asc = true } } ]

  let testName = "SelectExpr"
  let p = SqlParser.Select.selectQuery pzero |>> (fun x -> x.select)
  cases |> List.iteri (fun i -> parseStatementTest $"{testName}-{i}" p)

[<Fact>]
let notExists () =
  let subQuery =
    SubQuery
      { withAliases = []
        select = { empty with from = [ table "t0" ] } }

  let cases =
    [ "a AND NOT EXISTS (SELECT * FROM t0)", sand (col "a") (Not(Exists subQuery))
      "a AND EXISTS (SELECT * FROM t0)", sand (col "a") (Exists subQuery) ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"NotExists-{i}" (SqlParser.Scalar.scalarOp SqlParser.Select.withSelect))

[<Fact>]
let notInSelect () =
  let selectT0 =
    SubQuery
      { withAliases = []
        select = { empty with from = [ table "t0" ] } }

  let aNotInT0 = Not(In { left = col "a"; right = selectT0 })

  let cases =
    [ "a NOT IN (SELECT * FROM t0)", aNotInT0
      "a NOT IN (SELECT * FROM t0) AND a NOT IN (SELECT * FROM t0)", sand aNotInT0 aNotInT0 ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"notInSelect-{i}" (SqlParser.Scalar.scalarOp SqlParser.Select.withSelect))

[<Fact>]
let selectWith () =
  let cases =
    [ "WITH q0 AS (SELECT * FROM t0) SELECT * from t0,t1",
      { withAliases =
          [ { alias = "q0"
              select = { empty with from = [ table "t0" ] } } ]
        select =
          { empty with
              from = [ table "t0"; table "t1" ] } } ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"SelectWith-{i}" SqlParser.Select.withSelect)

[<Fact>]
let funcCoalesce () =
  let cases =
    [ "COALESCE((SELECT id FROM t0), '0')",
      Func
        { name = "COALESCE"
          args =
            [ SubQuery
                { withAliases = []
                  select =
                    { columns = [ col "id" ]
                      distinct = false
                      from = [ table "t0" ]
                      where = None
                      groupBy = []
                      orderBy = None
                      having = None
                      limit = None
                      offset = None } }
              String "0" ]
          window = None } ]

  cases
  |> List.iteri (fun i ->
    parseStatementTest $"funcCoalesce-{i}" (SqlParser.Scalar.columnOrFunc SqlParser.Select.withSelect))

[<Fact>]
let unionSelect () =
  let emptyWith = { withAliases = []; select = empty }

  let cases =
    [ "select * from t0 union select * from t1",
      [ { emptyWith with
            select.from = [ table "t0" ] }
        { emptyWith with
            select.from = [ table "t1" ] } ] ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"unionSelect-{i}" SqlParser.Select.unionSelect)
