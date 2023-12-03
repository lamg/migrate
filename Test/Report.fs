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

module Report

open Xunit
open Migrate
open Types
open DbUtil
open Migrate.Reports.Report
open SqlParser.Types
open Dapper.FSharp.SQLite

let exampleProject =
  { dbFile = ":memory:"
    schemaVersion = "0.0.1"
    versionRemarks = "test"
    reports = [ { src = "rel0"; dest = "rel0_report" } ]
    syncs = []
    pullScript = None
    source =
      { tables =
          [ { name = "rel0_report"
              columns =
                [ { name = "col0"
                    ``type`` = SqlInteger
                    constraints = [ NotNull ] }
                  { name = "col1"
                    ``type`` = SqlText
                    constraints = [ NotNull ] } ]
              constraints = [ Unique [ "col0" ] ] }
            { name = "rel0"
              columns =
                [ { name = "col0"
                    ``type`` = SqlInteger
                    constraints = [ NotNull ] }
                  { name = "col1"
                    ``type`` = SqlText
                    constraints = [ NotNull ] } ]
              constraints = [ Unique [ "col0" ] ] } ]
        views = []
        inserts = []
        indexes = [] } }

type Rel0 = { col0: int; col1: string }
let rel0Table = table'<Rel0> "rel0"
let rel0Report = table'<Rel0> "rel0_report"

[<Fact>]
let reportTest () =
  let dbFile = exampleProject.dbFile
  let tempDb = Execution.Commit.createTempDb exampleProject.source dbFile
  let p = { exampleProject with dbFile = tempDb }
  use conn = openConn tempDb

  insert {
    into rel0Report
    values [ { col0 = 0; col1 = "old_value" } ]
  }
  |> conn.InsertAsync<Rel0>
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> fun r -> Assert.Equal(1, r)

  insert {
    into rel0Table
    values [ { col0 = 0; col1 = "new_value" }; { col0 = 1; col1 = "x1" } ]
  }
  |> conn.InsertAsync<Rel0>
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> fun r -> Assert.Equal(2, r)

  syncReportsConn p conn

  let reportValues = getReportValues p

  let reportTable = p.source.tables.Head
  let cols = reportTable.columns

  let expected = [ cols, [ [ "0"; "new_value" ]; [ "1"; "x1" ] ] ]

  Assert.Equal<list<ColumnDef list * string list list>>(expected, reportValues)
