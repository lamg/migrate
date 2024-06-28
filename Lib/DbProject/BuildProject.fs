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

module internal Migrate.DbProject.BuildProject

open Migrate.Types
open Migrate.SqlParser

let collectSql (xs: SqlFile list) =
  let r =
    { tableSyncs = []
      tableInits = []
      tables = []
      views = []
      indexes = [] }

  xs
  |> List.fold
    (fun acc n ->
      { tableSyncs = acc.tableSyncs @ n.tableSyncs
        tableInits = acc.tableInits @ n.tableInits
        tables = acc.tables @ n.tables
        views = acc.views @ n.views
        indexes = acc.indexes @ n.indexes })
    r

let mergeTomlSql (p: DbTomlFile) (src: SqlFile) =
  { versionRemarks = p.versionRemarks
    dbFile = p.dbFile
    source = src
    syncs = p.syncs
    inits = p.inits
    reports = p.reports
    pullScript = p.pullScript
    schemaVersion = p.schemaVersion
    includeFsFiles = p.includeFsFiles }

let buildProject (reader: string -> string) (p: DbTomlFile) =
  let parse (file, sql) =
    match parseSql p.inits file sql with
    | Ok p -> p
    | Error e -> MalformedProject e |> raise

  let fileContent = p.files |> List.map (fun f -> f, reader f)
  let sql = fileContent |> List.map parse
  sql |> collectSql |> mergeTomlSql p
