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

module RunMigration

open Xunit
open Migrate
open Types
open SqlParser.Types

let emptySchema =
  { inserts = []
    tables = []
    views = []
    indexes = [] }

let emptyProject: Project =
  { dbFile = "test.sqlite3"
    schemaVersion = "0.0.0"
    versionRemarks = "empty project"
    source = emptySchema
    syncs = []
    reports = []
    pullScript = None }

let schema0 =
  { emptySchema with
      tables =
        [ { name = "table0"
            columns =
              [ { name = "col0"
                  ``type`` = SqlInteger
                  constraints = [ NotNull ] } ]
            constraints = [] } ] }

let removeFile f =
  if System.IO.File.Exists f then
    System.IO.File.Delete f
  else
    ()

[<Fact>]
let stepCalcTest () =
  let dbFile = emptyProject.dbFile
  let tempDb = Execution.Commit.createTempDb emptyProject.source dbFile
  let p = { emptyProject with dbFile = tempDb }

  Execution.Commit.replicateInDb schema0 p.dbFile

  let run () =
    use conn = DbUtil.openConn p.dbFile
    let r = Execution.Commit.migrateStep p conn
    removeFile "test.sqlite3"
    r

  match run () with
  | Some [ { reason = Removed "table0"
             statements = xs
             error = None } ] -> Assert.Equal<string list>([ "DROP TABLE table0" ], xs)
  | Some [ { statements = xs; error = Some e } ] -> Assert.Fail($"executing {xs} got error {e}")
  | None -> Assert.Fail "expected sql, got none"
  | v -> Assert.Fail $"got {v} instead the expected pattern"

[<Fact>]
let runMigrationTest () =
  Execution.Commit.dryMigration emptyProject

[<Fact>]
let getMigrationsTest () =
  let tempDb = Execution.Commit.createTempDb emptyProject.source emptyProject.dbFile
  let p0 = { emptyProject with dbFile = tempDb }

  let p =
    { p0 with
        source = schema0
        schemaVersion = "0.0.1" }

  Execution.Commit.migrateAndCommit p
  use conn = DbUtil.openConn p.dbFile
  let xs = MigrationStore.getMigrations conn
  removeFile p0.dbFile
  Assert.Equal(1, xs.Length)
  Assert.Equal("empty project", xs.Head.migration.versionRemarks)

[<Fact>]
let parseReasonTest () =
  let cases = [ Added "x"; Removed "y"; Changed("x", "y") ]

  cases
  |> List.iter (fun c ->
    let r = c.ToString() |> MigrationStore.parseReason
    Assert.Equal(c, r))
