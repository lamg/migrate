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

module Scalar

open Xunit
open FParsec
open Util
open SqlParser.Types
open SqlParser.Scalar
open SqlParser.Basic

module K = SqlParser.Keyword
module S = SqlParser.Symbol

[<Fact>]
let IntegerTest () =
    let case0 = "9", Integer 9
    let testName = "IntegerTest"
    parseStatementTest testName integer case0

[<Fact>]
let StringTest () =
    let case0 = "'hello'", String "hello"
    let testName = "StringTest"
    parseStatementTest testName string case0


[<Fact>]
let CaseWhenTest () =
    let case0 = "case when 1 then 2 else 3 end"
    let testName = "CaseWhenTest"

    let expected =
        CaseWhen
            { cond = Integer 1
              thenExpr = Integer 2
              elseExpr = Integer 3 }

    parseStatementTest testName (caseWhen pzero) (case0, expected)

[<Fact>]
let WindowTest () =
    let order =
        Some
            { columns = [ Util.var "v" "created_at" ]
              asc = false }

    let cases =
        [ "OVER (PARTITION BY n.subject_id ORDER BY v.created_at DESC)",
          { partitionBy = [ Util.var "n" "subject_id" ]
            orderBy = order }

          "OVER (ORDER BY v.created_at DESC)", { partitionBy = []; orderBy = order } ]

    cases |> List.iteri (fun i -> parseStatementTest $"WindowTest-{i}" window)

[<Fact>]
let FuncDateTest () =
    let testName = "FuncDateTest"

    let case0 =
        "date('now')",
        Func
            { name = "date"
              args = [ String "now" ]
              window = None }

    parseStatementTest testName (columnOrFunc pzero) case0

[<Fact>]
let BasicAnd () =
    let case0 = "a AND b", sand (col "a") (col "b")
    parseStatementTest "BasicAnd" (scalarOp pzero) case0

[<Fact>]
let BasicOr () =
    let case0 = "a OR b", sor (col "a") (col "b")
    parseStatementTest "BasicOr" (scalarOp pzero) case0


[<Fact>]
let OrAndEq () =
    let case0 =
        "a OR b AND c = d", sor (col "a") (sand (col "b") (eq (col "c") (col "d")))

    parseStatementTest "OrAndEq" (scalarOp pzero) case0

[<Fact>]
let OrAndEqParens () =
    let case0 =
        "(a) OR (b AND (c = d))", sor (col "a") (sand (col "b") (eq (col "c") (col "d")))

    parseStatementTest "OrAndEqEnclosed" (scalarOp pzero) case0

[<Fact>]
let Arithmetic () =
    let cases =
        [ "a > b", Gt { left = col "a"; right = col "b" }
          "a <= b", Lte { left = col "a"; right = col "b" } ]

    cases
    |> List.iteri (fun i -> parseStatementTest $"Arithmetic-{i}" (scalarOp pzero))


[<Fact>]
let BasicNot () =
    let case0 = "a AND NOT b", sand (col "a") (Not(col "b"))
    parseStatementTest "BasicNot" (scalarOp pzero) case0

[<Fact>]
let BasicConcat () =
    let case0 =
        "a AND b = c || d", sand (col "a") (eq (col "b") (concat (col "c") (col "d")))

    parseStatementTest "BasicConcat" (scalarOp pzero) case0

[<Fact>]
let BasicNotIn () =
    let case0 = "a NOT IN b", Not(In { left = col "a"; right = col "b" })
    parseStatementTest "BasicNotIn" (scalarOp pzero) case0

[<Fact>]
let Concat3 () =
    let case0 = "a || b || c", concat (col "a") (concat (col "b") (col "c"))
    parseStatementTest "Concat3" (scalarOp pzero) case0

[<Fact>]
let SumCaseWhen () =
    let testName = "SumCaseWhen"

    let case0 =
        "sum(
        CASE WHEN answer LIKE 'Yes%' THEN
            1
        ELSE
            0
        END
    )",
        Func
            { name = "sum"
              args =
                [ CaseWhen
                      { cond =
                          Like
                              { left = col "answer"
                                right = String "Yes%" }
                        thenExpr = Integer 1
                        elseExpr = Integer 0 } ]
              window = None }

    let p = columnOrFunc pzero |> token "column or func"
    parseStatementTest testName p case0

[<Fact>]
let ScalarEndKeyword () =
    let cases =
        [ "a END", (col "a", S.Composite [ K.End ])
          "a OR b GROUP BY", (sor (col "a") (col "b"), S.Composite [ K.Group; K.By ])
          "a OR b ORDER BY", (sor (col "a") (col "b"), S.Composite [ K.Order; K.By ]) ]

    let p =
        parse {
            let! s = scalarOp pzero

            let! endP =
                composite [ K.End ]
                <|> composite [ K.Group; K.By ]
                <|> composite [ K.Order; K.By ]

            return s, endP
        }

    cases |> List.iteri (fun i -> parseStatementTest $"ScalarEndKeyword-{i}" p)
