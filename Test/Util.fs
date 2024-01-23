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

open Migrate.Types

let setenv var value =
  System.Environment.SetEnvironmentVariable(var, value)

let emptySchema =
  { tableSyncs = []
    tableInits = []
    tables = []
    views = []
    indexes = [] }

let colInt name =
  { name = name
    columnType = SqlInteger
    constraints = [ PrimaryKey [] ] }

let colStr name =
  { name = name
    columnType = SqlText
    constraints = [ NotNull ] }

let colReal name =
  { name = name
    columnType = SqlReal
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
      tableSyncs = [ oneRowInsert ] }

let projectWithOneTable =
  { versionRemarks = "empty project"
    schemaVersion = "0.0.1"
    dbFile = "db.sqlite3"
    source = schemaWithOneTable
    syncs = [ "table0" ]
    inits = []
    reports = []
    pullScript = None }
