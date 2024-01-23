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

module TableSync

open Xunit

open Migrate.Types
open Migrate.Calculation.TableSync
open Util

[<Fact>]
let basicInsert () =
  let dbSchema =
    { schemaWithOneTable with
        tableSyncs = [ emptyInsert ] }

  let xs = tableSyncsMigration dbSchema projectWithOneTable

  let expected =
    [ { reason = Added "1"
        statements = [ "INSERT INTO table0(id, name) VALUES (1, 'one')" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

let insertRealEmpty =
  { table = "table0"
    columns = [ "id"; "v" ]
    values = [] }

let schemaWithReal =
  { emptySchema with
      tables =
        [ { name = "table0"
            columns = [ colInt "id"; colReal "v" ]
            constraints = [] } ]
      tableSyncs =
        [ { insertRealEmpty with
              values = [ [ Integer 1; Real 0.5 ] ] } ] }

[<Fact>]
let basicInsertWithReal () =
  let dbSchema =
    { schemaWithReal with
        tableSyncs = [ insertRealEmpty ] }

  let xs =
    tableSyncsMigration
      dbSchema
      { projectWithOneTable with
          source = schemaWithReal }

  let expected =
    [ { reason = Added "1"
        statements = [ "INSERT INTO table0(id, v) VALUES (1, 0.5)" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let basicUpdate () =
  let dbSchema =
    { schemaWithOneTable with
        tableSyncs =
          [ { oneRowInsert with
                values = [ [ Integer 1; String "zero" ] ] } ] }

  let xs = tableSyncsMigration dbSchema projectWithOneTable

  let expected =
    [ { reason = Changed("zero", "one")
        statements = [ "UPDATE table0 SET name = 'one' WHERE id = 1" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let basicDelete () =
  let dbSchema =
    { schemaWithOneTable with
        tableSyncs = [ oneRowInsert ] }

  let xs =
    tableSyncsMigration
      dbSchema
      { projectWithOneTable with
          source.tableSyncs = [ emptyInsert ] }

  let expected =
    [ { reason = Removed "1"
        statements = [ "DELETE FROM table0 WHERE id = 1" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let basicInsertWithKeyInPos2 () =
  let schema =
    { emptySchema with
        tables =
          [ { name = "table0"
              columns = [ colInt "id"; colStr "name" ]
              constraints = [] } ]
        tableSyncs = [ emptyInsert ] }

  let projectSchema =
    { schema with
        tableSyncs =
          [ { table = "table0"
              columns = [ "name"; "id" ]
              values = [ [ String "one"; Integer 1 ] ] } ] }

  let project =
    { projectWithOneTable with
        source = projectSchema
        syncs = [ "table0" ] }

  let xs = tableSyncsMigration schema project

  let expected =
    [ { reason = Added "1"
        statements = [ "INSERT INTO table0(id, name) VALUES (1, 'one')" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)
