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

module DbProject

open Migrate.Types
open SqlParser.Types
open Migrate.DbProject.ParseDbToml
open Migrate.DbProject.BuildProject
open Xunit
open Util

let project0 =
  """
schema_version = "0.0.1"
version_remarks = "project initialization"
db_file = "db"

files = ["file0.sql", "file1.sql"]

sync = ["query"]

[[report]]
src = "source_relation"
dest = "destination_relation"
"""

let project1 =
  """
schema_version = "0.0.1"
version_remarks = "project initialization"
files = ["file0.sql", "file1.sql"]

sync = ["query"]

[[report]]
src = "source_relation"
dest = "destination_relation"
"""

[<Fact>]
let parseProjectSrc () =
  setenv "db" "/data/db.sqlite3"

  let p = parseDbToml project0

  let expected =
    { schemaVersion = "0.0.1"
      versionRemarks = "project initialization"
      dbFile = "/data/db.sqlite3"
      syncs = [ "query" ]
      files = [ "file0.sql"; "file1.sql" ]
      pullScript = None
      reports =
        [ { src = "source_relation"
            dest = "destination_relation" } ] }

  Assert.Equal(expected, p)

  setenv "db" ""

[<Fact>]
let notFoundDb () =
  try
    parseDbToml project0 |> ignore
    failwith "it should throw an exception because db is not defined"
  with MalformedProject e ->
    Assert.Equal("db.toml db_file: environment variable 'db' not defined", e)

[<Fact>]
let notDefinedDbPathVar () =
  try
    parseDbToml project1 |> ignore
    failwith "it should throw an exception because db_file is not defined"
  with MalformedProject e ->
    Assert.Equal("no db_file defined", e)


[<Fact>]
let wrapWithProject () =
  let p =
    { versionRemarks = "project initialization"
      schemaVersion = "0.0.1"
      dbFile = "/data/db.sqlite3"
      syncs = [ "table0" ]
      files = [ "file0.sql"; "file1.sql" ]
      pullScript = None
      reports =
        [ { src = "source_relation"
            dest = "destination_relation" } ] }

  let src v : SqlFile =
    { inserts =
        [ { table = "table0"
            columns = [ "id"; "name" ]
            values = [ [ Integer 0; v ] ] } ]
      tables = []
      views = []
      indexes = [] }

  let expected: Project =
    { versionRemarks = "project initialization"
      schemaVersion = "0.0.1"
      dbFile = "/data/db.sqlite3"
      syncs = [ "table0" ]
      source = src (String "value0")
      pullScript = None
      reports =
        [ { src = "source_relation"
            dest = "destination_relation" } ] }

  setenv "env0" "value0"

  let f =
    mergeTomlSql
      p
      (src (
        EnvVar
          { qualifier = None
            ``member`` = "env0" }
      ))

  Assert.Equal(expected, f)
