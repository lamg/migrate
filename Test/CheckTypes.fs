// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use _ file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module CheckTypes

open Migrate.Types
open Migrate.Checks.Types
open Xunit
open Util

let exampleView =
  """
CREATE VIEW view0 AS
SELECT t.col0 AS x, v.col1 AS y FROM table0 t JOIN table0 v ON t.col0 = v.col0
"""
  |> Migrate.SqlParser.parseSql [] "Calculation.fs"
  |> function
    | Ok f -> f
    | Error e -> failwith e

let exampleProject =
  { dbFile = ":memory:"
    schemaVersion = "0.0.1"
    versionRemarks = "test"
    reports = []
    syncs = []
    inits = []
    pullScript = None
    source =
      { tables =
          [ { name = "table0"
              columns =
                [ { name = "col0"
                    columnType = SqlInteger
                    constraints = [ NotNull ] }
                  { name = "col1"
                    columnType = SqlText
                    constraints = [ NotNull ] } ]
              constraints = [ Unique [ "col0" ] ] } ]
        views = exampleView.views
        tableSyncs = []
        tableInits = []
        indexes = [] } }

[<Fact>]
let checkTypes () =
  let types, errs = typeCheck exampleProject.source
  Assert.Equal<string list>([], errs)
  let rels = relationTypes types

  let expected =
    [ { name = "table0"
        columns = [ "col0", SqlInteger; "col1", SqlText ] }
      { name = "view0"
        columns = [ "x", SqlInteger; "y", SqlText ] } ]

  Assert.Equal<Relation list>(expected, rels)
