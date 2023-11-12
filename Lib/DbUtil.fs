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

module internal Migrate.DbUtil

open Microsoft.Data.Sqlite
open Types
open System.IO

let openConn (dbFile: string) =
  let createFile (dbFile: string) =
    let dir = Path.GetDirectoryName dbFile
    let notExists = Directory.Exists >> not

    if notExists dir && dir <> "" then
      Directory.CreateDirectory dir |> ignore

    File.Create dbFile |> ignore

  let notExist = File.Exists >> not

  if notExist dbFile then
    createFile dbFile

  let connStr = $"Data Source={dbFile};Mode=ReadWriteCreate"

  try
    let conn = new SqliteConnection(connStr)
    conn.Open()
    conn
  with :? SqliteException as e ->
    FailedOpenDb { dbFile = dbFile; msg = e.Message } |> raise

let runSql (conn: SqliteConnection) (sql: string) =
  try
    let c = conn.CreateCommand()

    c.CommandText <- sql
    c.ExecuteNonQuery() |> ignore
  with :? SqliteException as e ->
    FailedQuery { sql = sql; error = e.Message } |> raise

let runSqlTx (conn: SqliteConnection) (commands: string list) =
  let tx = conn.BeginTransaction()

  try
    commands
    |> List.iter (fun c ->
      let x = conn.CreateCommand()
      x.CommandText <- c
      x.ExecuteNonQuery() |> ignore)

    tx.Commit()
    Ok()
  with :? SqliteException as e ->
    tx.Rollback()
    Error e.Message
