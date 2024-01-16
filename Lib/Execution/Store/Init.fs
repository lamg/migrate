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
