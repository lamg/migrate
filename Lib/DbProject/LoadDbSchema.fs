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

module Migrate.DbProject.LoadDbSchema

open Migrate.Types
open Microsoft.Data.Sqlite
open System.Data
open Migrate.DbUtil
open SqlParser.Types
open Dapper.FSharp.SQLite

let rowReader (c: CreateTable) (rd: IDataReader) =
  c.columns
  |> List.mapi (fun i c ->
    match c with
    | { ``type`` = SqlText } -> rd.GetString i |> String
    | { ``type`` = SqlInteger } -> rd.GetInt32 i |> Integer)

let tableValues (conn: SqliteConnection) (ct: CreateTable) =
  let cols = ct.columns |> List.map (fun c -> c.name)
  let joinedCols = cols |> Migrate.SqlGeneration.Util.sepComma id

  let query = $"SELECT {joinedCols} FROM {ct.name}"
  let c = conn.CreateCommand()
  c.CommandText <- query
  let rd = c.ExecuteReader()


  let vss =
    seq {
      while rd.Read() do
        let vs = rowReader ct rd
        yield vs
    }
    |> Seq.toList

  { table = ct.name
    columns = cols
    values = vss }

let searchTable (f: SqlFile) (name: string) =
  f.tables |> List.tryFind (fun t -> t.name = name)

let searchColumns (f: SqlFile) (table: string) =
  searchTable f table |> Option.map (fun t -> t.columns)

type SqliteMaster = { sql: string }
let sqliteMaster = table'<SqliteMaster> "sqlite_master"

[<Literal>]
let migrateTablePrefix = "github_com_lamg_migrate_"

let sqliteMasterStatements (p: Project) =
  use conn = openConn p.dbFile

  select {
    for r in sqliteMaster do
      where (r.sql <> null)
  }
  |> conn.SelectAsync<SqliteMaster>
  |> Async.AwaitTask
  |> Async.RunSynchronously
  |> Seq.toList


let dbSchemaList (p: Project) =
  let hasNoSubstr (x: string) (xs: string list) =
    xs |> List.exists (fun s -> x.Contains s) |> not

  sqliteMasterStatements p
  |> List.choose (function
    | { sql = sql } when hasNoSubstr sql [ "sqlite_sequence"; migrateTablePrefix ] -> Some sql
    | _ -> None)

let rawDbSchema (p: Project) =
  dbSchemaList p |> Migrate.Print.joinSqlPretty

let dbSchema (p: Project) =
  let empty =
    { tables = []
      views = []
      inserts = []
      indexes = [] }

  use conn = openConn p.dbFile

  let schema =
    dbSchemaList p
    |> function
      | [] -> empty
      | xs ->
        xs
        |> Migrate.Print.joinSql
        |> SqlParser.Parser.parseSql p.dbFile
        |> function
          | Ok f -> f
          | Error e -> FailedParse e |> raise

  let schemaWithIns =
    p.syncs
    |> List.choose (fun ts ->
      p.source.tables
      |> List.tryFind (fun n -> n.name = ts)
      |> Option.map (tableValues conn))
    |> (fun ins -> { schema with inserts = ins })

  schemaWithIns

let migrationSchema (p: Project) =
  sqliteMasterStatements p
  |> List.choose (function
    | { sql = sql } when sql.Contains migrateTablePrefix -> Some sql
    | _ -> None)
  |> function
    | [] -> None
    | xs ->
      xs
      |> Migrate.Print.joinSql
      |> SqlParser.Parser.parseSql p.dbFile
      |> function
        | Ok f -> Some f
        | Error e -> FailedParse $"Loading migration tables:\n{e}" |> raise
