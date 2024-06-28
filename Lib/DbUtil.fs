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

open System
open System.IO
open System.Text.RegularExpressions
open System.Data
open System.Reflection
open Microsoft.Data.Sqlite

open Types
open Print

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

let joinSql (xs: string list) =
  xs |> String.concat ";\n" |> (fun s -> $"{s};")

let joinSqlPretty xs =
  xs |> List.map (SqlPrettify.SqlPrettify.Pretty >> _.TrimEnd()) |> joinSql

let colorizeSql sql =
  let keywordPattern =
    @"\b(SELECT|FROM|WHERE|GROUP BY|ORDER BY|CREATE|VIEW|TABLE|UNIQUE|PRIMARY KEY|INDEX|AS|WITH|NOT|NULL|AND|OR|LIMIT|OFFSET|ON|LIKE|IN|EXISTS|COALESCE|FOREIGN KEY|REFERENCES)\b"

  let matchEvaluator (m: Match) =
    let ansiGreen = "\x1b[32m"
    let ansiReset = "\x1b[0m"
    $"%s{ansiGreen}%s{m.Value}%s{ansiReset}"

  Regex.Replace(sql, keywordPattern, matchEvaluator, RegexOptions.IgnoreCase)

let printQueryErr (e: QueryError) =
  printYellow "running:"
  printfn $"{e.sql}"
  printYellow "got error"
  printRed $"{e.error}"

let printResources (asm: Assembly) =
  asm.GetManifestResourceNames() |> Array.iter (printfn "RESOURCE: %s")

let loadFromRes (asm: Assembly) (namespaceForResx: string) (file: string) =
  let namespaceDotFile = $"{namespaceForResx}.{file}"

  try
    use stream = asm.GetManifestResourceStream namespaceDotFile
    use file = new StreamReader(stream)
    (namespaceDotFile, file.ReadToEnd())
  with ex ->
    FailedLoadResFile $"failed loading resource file {namespaceDotFile}: {ex.Message}"
    |> raise