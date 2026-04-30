module internal MigLib.Commands.Init.SchemaInit

open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib.Commands.Types


let initializeDatabaseFromSchemaOnly
  (newConnection: SqliteConnection)
  (targetSchema: SqlFile)
  : Task<Result<int64, MigError>> =
  failwith "TODO initializeDatabaseFromSchemaOnly"
