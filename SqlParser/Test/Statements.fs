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

module Statements

open Xunit
open SqlParser.Types
open Util

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
let statement () =
  let cases =
    [ "CREATE TABLE t0(id integer NOT NULL); CREATE VIEW v0 AS SELECT * FROM t0;",
      { tables =
          [ { name = "t0"
              columns =
                [ { name = "id"
                    ``type`` = SqlInteger
                    constraints = [ NotNull ] } ]
              constraints = [] } ]
        views =
          [ { name = "v0"
              select =
                { withAliases = []
                  select = { empty with from = [ table "t0" ] } } } ]
        inserts = []
        indexes = [] } ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"statement-{i}" SqlParser.Statements.statements)
