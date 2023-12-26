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

let emptySchema =
  { inserts = []
    tables = []
    views = []
    indexes = [] }

let colInt name =
  { name = name
    columnType = SqlInteger
    constraints = [ PrimaryKey [] ] }

let colStr name =
  { name = name
    columnType = SqlInteger
    constraints = [ NotNull ] }

let emptyInsert: InsertInto =
  { table = "table0"
    columns = [ "id"; "name" ]
    values = [] }

let oneRowInsert: InsertInto =
  { emptyInsert with
      values = [ [ Integer 1; String "one" ] ] }

let schemaWithOneTable =
  { emptySchema with
      tables =
        [ { name = "table0"
            columns = [ colInt "id"; colStr "name" ]
            constraints = [] } ]
      inserts = [ oneRowInsert ] }


let emptyProject =
  { versionRemarks = "empty project"
    schemaVersion = "0.0.1"
    dbFile = "db.sqlite3"
    source = schemaWithOneTable
    syncs = [ "table0" ]
    reports = []
    pullScript = None }

[<Fact>]
let basicInsert () =
  let dbSchema =
    { schemaWithOneTable with
        inserts = [ emptyInsert ] }

  let xs = insertsMigration dbSchema emptyProject

  let expected =
    [ { reason = Added "1"
        statements = [ "INSERT INTO table0(id, name) VALUES (1, 'one')" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let basicUpdate () =
  let dbSchema =
    { schemaWithOneTable with
        inserts =
          [ { oneRowInsert with
                values = [ [ Integer 1; String "zero" ] ] } ] }

  let xs = insertsMigration dbSchema emptyProject

  let expected =
    [ { reason = Changed("zero", "one")
        statements = [ "UPDATE table0 SET name = 'one' WHERE id = 1" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)

[<Fact>]
let basicDelete () =
  let dbSchema =
    { schemaWithOneTable with
        inserts = [ oneRowInsert ] }

  let xs =
    insertsMigration
      dbSchema
      { emptyProject with
          source.inserts = [ emptyInsert ] }

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
        inserts = [ emptyInsert ] }

  let projectSchema =
    { schema with
        inserts =
          [ { table = "table0"
              columns = [ "name"; "id" ]
              values = [ [ String "one"; Integer 1 ] ] } ] }

  let project =
    { emptyProject with
        source = projectSchema
        syncs = [ "table0" ] }

  let xs = insertsMigration schema project

  let expected =
    [ { reason = Added "1"
        statements = [ "INSERT INTO table0(id, name) VALUES (1, 'one')" ] } ]

  Assert.Equal<SolverProposal list>(expected, xs)
