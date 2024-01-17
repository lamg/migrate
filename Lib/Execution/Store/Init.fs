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

module Migrate.Execution.Store.Init

open Migrate
open Migrate.Types
open Microsoft.Data.Sqlite
open Migrate.DbProject
open Migrate.DbUtil

/// <summary>
/// Initializes tables storing migrations in the database
/// Raises FailedOpenStore and FailedOpenDb
/// In case the tables dedicated for storing migration metadata don't have
/// the same schema as the one expected by the migration tool, it raises FailedOpenStore
/// </summary>
/// <param name="conn"></param>
let initStore (conn: SqliteConnection) =
  let dbFile = conn.DataSource

  let _, referenceStoreSchema =
    System.Reflection.Assembly.GetExecutingAssembly()
    |> fun asm ->
      try
        loadFromRes asm "Migrate.Execution.Store" "schema.sql"
      with FailedLoadResFile e ->
        FailedOpenStore e |> raise

  let refSchema =
    match SqlParser.parseSql [] dbFile referenceStoreSchema with
    | Ok f -> f
    | Error e -> FailedOpenStore e |> raise

  let storeSchema = LoadDbSchema.migrationSchema conn

  match storeSchema with
  | None -> runSql conn referenceStoreSchema
  | Some s when s = refSchema -> ()
  | Some s ->
    FailedOpenStore $"store schema mismatch:\n{dbFile} has\n{s}\nwhile mig expects:\n{refSchema} "
    |> raise
