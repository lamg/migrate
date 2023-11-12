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

module Util

open FParsec
open SqlParser.Types
open Xunit

let parseStatementTest (testName: string) (p: Parser<'a, unit>) ((sql, r): string * 'a) =
    match runParserOnString p () testName sql with
    | Success(v, _, _) ->
        if r = v then
            ()
        else
            Assert.Fail $"failed {testName}:\nexpected\n{r}\nbut got\n{v}"
    | Failure(e, _, _) -> Assert.Fail e

let expectFailure (testName: string) (sql: string) (p: Parser<'a, unit>) =
    match runParserOnString p () testName sql with
    | Success _ -> Assert.Fail $"failed {testName}:\nexpected failure but succeeded"
    | Failure _ -> ()

let checkFailure (testName: string) (p: Parser<'a, unit>) ((sql, expected): string * string) =
    match runParserOnString p () testName sql with
    | Success _ -> Assert.Fail $"failed {testName}:\nexpected failure but succeeded"
    | Failure(e, _, _) -> Assert.Contains(expected, e)

let sor l r = Or { left = l; right = r }
let sand l r = And { left = l; right = r }
let eq l r = Eq { left = l; right = r }
let concat l r = Concat { left = l; right = r }

let table t =
    Table { qualifier = None; ``member`` = t }

let tableAlias t a = Alias { expr = table t; alias = a }

let column t c =
    Column { qualifier = Some t; ``member`` = c }

let columnAlias t c a = Alias { expr = column t c; alias = a }

let var t c = { qualifier = Some t; ``member`` = c }

let col c =
    Column { qualifier = None; ``member`` = c }

let envVar x =
    EnvVar { qualifier = None; ``member`` = x }

let equalSelect (x: Select) (y: Select) =
    Assert.Equal(x.distinct, y.distinct)
    Assert.Equal<Expr list>(x.columns, y.columns)
    Assert.Equal<Expr list>(x.from, y.from)
    Assert.Equal(x.where, y.where)
    Assert.Equal<Var list>(x.groupBy, y.groupBy)
    Assert.Equal(x.having, y.having)
    Assert.Equal(x.orderBy, y.orderBy)
    Assert.Equal(x.limit, y.limit)
    Assert.Equal(x.offset, y.offset)

let equalWithSelect (x: WithSelect) (y: WithSelect) =
    List.zip x.withAliases y.withAliases
    |> List.iter (fun ({ alias = a; select = s }, { alias = b; select = t }) ->
        Assert.Equal(a, b)
        equalSelect s t)

    equalSelect x.select y.select
