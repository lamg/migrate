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
open Migrate.SqlParser
open Migrate.SqlParser.Types
open Dapper.FSharp.SQLite

let relationValues (conn: SqliteConnection) (relation: string) (cols: string list) (readRow: IDataReader -> Expr list) =
  let joinedCols = cols |> Migrate.SqlGeneration.Util.sepComma id

  let query = $"SELECT {joinedCols} FROM {relation}"
  let c = conn.CreateCommand()
  c.CommandText <- query
  let rd = c.ExecuteReader()


  let vss =
    seq {
      while rd.Read() do
        let vs = readRow rd
        yield vs
    }
    |> Seq.toList

  { table = relation
    columns = cols
    values = vss }

let rowReader (xs: SqlType list) (rd: IDataReader) =
  xs
  |> List.mapi (fun i c ->
    match c with
    | SqlText -> rd.GetString i |> String
    | SqlInteger -> rd.GetInt32 i |> Integer)

let tableValues (conn: SqliteConnection) (ct: CreateTable) =
  let cols = ct.columns |> List.map _.name
  let types = ct.columns |> List.map _.``type``
  let readRow = rowReader types

  try
    relationValues conn ct.name cols readRow
  with :? SqliteException as e ->
    if e.Message.Contains "no such table" then
      { table = ct.name
        columns = cols
        values = [] }
    else
      raise e

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

let rawDbSchema (p: Project) = dbSchemaList p |> joinSqlPretty

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
        |> joinSql
        |> Parser.parseSql p.dbFile
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
      |> joinSql
      |> Parser.parseSql p.dbFile
      |> function
        | Ok f -> Some f
        | Error e -> FailedParse $"Loading migration tables:\n{e}" |> raise
