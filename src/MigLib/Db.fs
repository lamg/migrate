namespace MigLib.Db

open System
open System.IO
open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib.TaskResult

[<AutoOpen>]
module Facade =
  [<Literal>]
  let Rfc3339UtcNow = "strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')"

  [<Literal>]
  let DefaultDatabaseInstance = "main"

  let resolveDatabaseInstance (instance: string option) =
    match instance with
    | Some value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
    | _ -> DefaultDatabaseInstance

  let private validateDatabaseFileSegment label (value: string) =
    let trimmed = value.Trim()

    if String.IsNullOrWhiteSpace trimmed then
      Error $"Database {label} is empty."
    elif trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 then
      Error $"Database {label} '{trimmed}' contains invalid file name characters."
    else
      Ok trimmed

  let buildSchemaBoundDbFileName (app: string) (instance: string option) (schemaHash: string) : Result<string, string> =
    result {
      let! validatedApp = validateDatabaseFileSegment "app" app
      let! validatedInstance = resolveDatabaseInstance instance |> validateDatabaseFileSegment "instance"

      if String.IsNullOrWhiteSpace schemaHash then
        return! Error "Schema hash is empty."
      else
        return $"{validatedApp}-{validatedInstance}-{schemaHash}.sqlite"
    }

  let tryParseSchemaBoundDbFileName (app: string) (instance: string option) (filePathOrName: string) =
    result {
      let! validatedApp = validateDatabaseFileSegment "app" app
      let! validatedInstance = resolveDatabaseInstance instance |> validateDatabaseFileSegment "instance"
      let fileName = Path.GetFileName filePathOrName

      if not (fileName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) then
        return None
      else
        let expectedPrefix = $"{validatedApp}-{validatedInstance}-"
        let fileStem = Path.GetFileNameWithoutExtension fileName

        if not (fileStem.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) then
          return None
        else
          let hashSegment = fileStem.Substring expectedPrefix.Length

          if hashSegment.Length = 16 && (hashSegment |> Seq.forall Uri.IsHexDigit) then
            return Some hashSegment
          else
            return None
    }

  type AutoIncPKAttribute = Attributes.AutoIncPKAttribute
  type PKAttribute = Attributes.PKAttribute
  type UniqueAttribute = Attributes.UniqueAttribute
  type DefaultAttribute = Attributes.DefaultAttribute
  type DefaultExprAttribute = Attributes.DefaultExprAttribute
  type IndexAttribute = Attributes.IndexAttribute
  type SelectAllAttribute = Attributes.SelectAllAttribute
  type SelectOneAttribute = Attributes.SelectOneAttribute
  type SelectByAttribute = Attributes.SelectByAttribute
  type SelectOneByAttribute = Attributes.SelectOneByAttribute
  type SelectLikeAttribute = Attributes.SelectLikeAttribute
  type SelectByOrInsertAttribute = Attributes.SelectByOrInsertAttribute
  type UpdateByAttribute = Attributes.UpdateByAttribute
  type DeleteByAttribute = Attributes.DeleteByAttribute
  type DeleteAllAttribute = Attributes.DeleteAllAttribute
  type InsertOrIgnoreAttribute = Attributes.InsertOrIgnoreAttribute
  type UpsertAttribute = Attributes.UpsertAttribute
  type FKAttribute = Attributes.FKAttribute
  type OnDeleteCascadeAttribute = Attributes.OnDeleteCascadeAttribute
  type OnDeleteSetNullAttribute = Attributes.OnDeleteSetNullAttribute
  type ViewAttribute = Attributes.ViewAttribute
  type JoinAttribute = Attributes.JoinAttribute
  type ViewSqlAttribute = Attributes.ViewSqlAttribute
  type OrderByAttribute = Attributes.OrderByAttribute
  type PreviousNameAttribute = Attributes.PreviousNameAttribute
  type DropColumnAttribute = Attributes.DropColumnAttribute

  let openSqliteConnection = Core.openSqliteConnection
  let resolveDatabaseFilePath = Core.resolveDatabaseFilePath

  type StartupDatabaseState =
    | Missing
    | Ready
    | Migrating
    | Invalid of reason: string

  type StartupDatabaseDecision =
    | UseExisting of dbPath: string
    | WaitForMigration of dbPath: string
    | MigrateThisInstance of dbPath: string
    | ExitEarly of dbPath: string * reason: string

  let getStartupDatabaseState (dbPath: string) =
    task {
      let! result = Startup.getStartupDatabaseState dbPath

      return
        result
        |> Result.map (function
          | Startup.Missing -> Missing
          | Startup.Ready -> Ready
          | Startup.Migrating -> Migrating
          | Startup.Invalid reason -> Invalid reason)
    }

  let getStartupDatabaseDecision (configuredDirectory: string) (dbFileName: string) =
    task {
      let! result = Startup.getStartupDatabaseDecision configuredDirectory dbFileName

      return
        result
        |> Result.map (function
          | Startup.UseExisting dbPath -> UseExisting dbPath
          | Startup.WaitForMigration dbPath -> WaitForMigration dbPath
          | Startup.MigrateThisInstance dbPath -> MigrateThisInstance dbPath
          | Startup.ExitEarly(dbPath, reason) -> ExitEarly(dbPath, reason))
    }

  let waitForStartupDatabaseReady = Startup.waitForStartupDatabaseReady
