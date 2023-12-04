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

module Migration

open Migrate.Types
open Migrate.Calculation.Migration
open SqlParser.Types
open Xunit

let emptySchema =
  { inserts = []
    tables = []
    views = []
    indexes = [] }

let emptyProject =
  { versionRemarks = "empty project"
    schemaVersion = "0.0.0"
    dbFile = "db.sqlite3"
    source = emptySchema
    syncs = []
    reports = []
    pullScript = None }

let schemaWithOneTable (tableName: string) =
  { emptySchema with
      tables =
        [ { name = tableName
            columns =
              [ { name = "id"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] } ]
            constraints = [] } ] }

let schemaWithUnique (tableName: string) =
  { emptySchema with
      tables =
        [ { name = tableName
            columns =
              [ { name = "id"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] } ]
            constraints = [ Unique [ "id" ] ] } ] }

let schemaWithView (viewName: string) =
  { emptySchema with
      views =
        [ { name = viewName
            selectUnion =
              [ { withAliases = []
                  select =
                    { columns = []
                      distinct = false
                      from =
                        [ Table
                            { qualifier = None
                              ``member`` = "table0" } ]
                      where = None
                      groupBy = []
                      having = None
                      orderBy = None
                      limit = None
                      offset = None } } ] } ] }

let schemaWithTwoCols =
  { emptySchema with
      tables =
        [ { name = "table0"
            columns =
              [ { name = "id"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] }
                { name = "column1"
                  ``type`` = SqlText
                  constraints = [ NotNull; Default(String "bla") ] } ]
            constraints = [] } ] }

let schemaWithTwoColsNewName =
  { emptySchema with
      tables =
        [ { name = "table0"
            columns =
              [ { name = "id"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] }
                { name = "column2"
                  ``type`` = SqlText
                  constraints = [ NotNull; Default(String "bla") ] } ]
            constraints = [] } ] }

let insertWithVar =
  { table = "table0"
    columns = [ "id"; "env_var" ]
    values = [ [ Integer 0; String "value0" ]; [ Integer 1; String "value1" ] ] }

let schemaWithInsert =
  { emptySchema with
      inserts = [ insertWithVar ] }

[<Fact>]
let emptyMigration () =
  let p = emptyProject
  let r = migration emptySchema p
  let expected = None
  Assert.Equal(expected, r)

[<Fact>]
let addTable () =
  let p =
    { emptyProject with
        source = schemaWithOneTable "table0" }

  let r = migration emptySchema p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Added "table0"
          statements = [ "CREATE TABLE table0(id integer NOT NULL)" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let possibleRenameTable () =
  let p =
    { emptyProject with
        source = schemaWithOneTable "table1" }

  let dbSchema = schemaWithOneTable "table0"
  let r = migration dbSchema p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "table0"
          statements = [ "DROP TABLE table0" ] }
        { reason = Added "table1"
          statements = [ "CREATE TABLE table1(id integer NOT NULL)" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let addView () =
  let p =
    { emptyProject with
        source = schemaWithView "view1" }

  let dbSchema = schemaWithView "view0"
  let r = migration dbSchema p

  let sqlView0 =
    Migrate.SqlGeneration.View.sqlCreateView dbSchema.views.Head
    |> Migrate.Print.joinSqlPretty

  let sqlView1 =
    Migrate.SqlGeneration.View.sqlCreateView p.source.views.Head
    |> Migrate.Print.joinSqlPretty

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "view0"
          statements = [ "DROP VIEW view0" ] }
        { reason = Added "view1"
          statements = [ "CREATE VIEW view1 AS\nSELECT * FROM table0" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let removeView () =
  let p = emptyProject

  let dbSchema = schemaWithView "view0"
  let r = migration dbSchema p

  let sqlView =
    Migrate.SqlGeneration.View.sqlCreateView dbSchema.views.Head
    |> Migrate.Print.joinSqlPretty

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "view0"
          statements = [ "DROP VIEW view0" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let renameView () =
  let p =
    { emptyProject with
        source = schemaWithView "view1" }

  let dbSchema = schemaWithView "view0"
  let r = migration dbSchema p

  let sqlView0 =
    Migrate.SqlGeneration.View.sqlCreateView dbSchema.views.Head
    |> Migrate.Print.joinSqlPretty

  let sqlView1 =
    Migrate.SqlGeneration.View.sqlCreateView p.source.views.Head
    |> Migrate.Print.joinSqlPretty

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "view0"
          statements = [ "DROP VIEW view0" ] }
        { reason = Added "view1"
          statements = [ "CREATE VIEW view1 AS\nSELECT * FROM table0" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let addColumn () =
  let p =
    { emptyProject with
        source = schemaWithTwoCols }

  let r = migration (schemaWithOneTable "table0") p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Added "column1 text"
          statements = [ "ALTER TABLE table0 ADD COLUMN column1 text NOT NULL DEFAULT 'bla'" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let dropColumn () =
  let p =
    { emptyProject with
        source = schemaWithOneTable "table0" }

  let r = migration schemaWithTwoCols p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "column1 text"
          statements = [ "ALTER TABLE table0 DROP COLUMN column1" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let possibleRenameColumn () =
  let p =
    { emptyProject with
        source = schemaWithTwoColsNewName }

  let r = migration schemaWithTwoCols p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Removed "column1 text"
          statements = [ "ALTER TABLE table0 DROP COLUMN column1" ] }
        { reason = Added "column2 text"
          statements = [ "ALTER TABLE table0 ADD COLUMN column2 text NOT NULL DEFAULT 'bla'" ] } ]

  Assert.Equal(expected, r)

[<Fact>]
let addUniqueConstraint () =
  let p =
    { emptyProject with
        source = schemaWithUnique "table0" }

  let r = migration (schemaWithOneTable "table0") p

  let expected: list<SolverProposal> option =
    Some
      [ { reason = Added "UNIQUE(id)"
          statements =
            [ "CREATE TABLE table0_aux(id integer NOT NULL, UNIQUE(id))"
              "INSERT OR IGNORE INTO table0_aux(id) SELECT id FROM table0"
              "DROP TABLE table0"
              "ALTER TABLE table0_aux RENAME TO table0" ] } ]

  Assert.Equal(expected, r)


[<Fact>]
let uniqueToPrimaryKey () =
  let schema0 = schemaWithUnique "table0"
  let table0 = schema0.tables.Head
  let column0 = table0.columns.Head

  let table1 =
    { table0 with
        columns =
          [ { column0 with
                constraints = [ PrimaryKey None ] } ] }

  let schema1 = { schema0 with tables = [ table1 ] }
  let p = { emptyProject with source = schema1 }
  let r = migration schema0 p
  r.IsSome |> Assert.True
  let v = r.Value
  Assert.Equal(v.Head.reason, Changed("id integer NOT NULL", "id integer PRIMARY KEY"))
