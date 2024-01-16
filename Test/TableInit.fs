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

module TableInit

open Xunit

open Migrate.Types
open Migrate.Calculation.Migration

open Util

[<Fact>]
let basicInit () =
  let dbSchema =
    { schemaWithOneTable with
        tableInits = [ emptyInsert ] }

  let project =
    { projectWithOneTable with
        source.tableInits = [ oneRowInsert ]
        inits = [ "table0" ] }

  let xs = tableInitsMigration dbSchema project

  let expected =
    [ { reason = Added "(1, 'one')"
        statements = [ "INSERT INTO table0(id, name) VALUES\n(1, 'one')" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let alreadyInitialized () =
  let dbSchema =
    { schemaWithOneTable with
        tableInits = [ oneRowInsert ] }

  let project =
    { projectWithOneTable with
        source.tableInits = [ oneRowInsert ]
        inits = [ "table0" ] }

  let xs = tableInitsMigration dbSchema project
  Assert.Equal(0, xs.Length)
