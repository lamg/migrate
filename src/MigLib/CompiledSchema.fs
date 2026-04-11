module MigLib.CompiledSchema

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.Types
open Mig.HotMigration
open MigLib.Db
open MigLib.Util

type GeneratedSchemaModule =
  { schema: SqlFile
    schemaIdentity: SchemaIdentity option
    schemaHash: string option
    dbApp: string option
    defaultDbInstance: string option }

let private staticBindingFlags =
  BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

let private tryGetStaticMemberValue (moduleType: Type) (memberName: string) =
  let propertyInfo = moduleType.GetProperty(memberName, staticBindingFlags)

  if not (isNull propertyInfo) then
    Ok(Some(propertyInfo.GetValue null))
  else
    let fieldInfo = moduleType.GetField(memberName, staticBindingFlags)

    if not (isNull fieldInfo) then
      Ok(Some(fieldInfo.GetValue null))
    else
      Ok None

let private tryReadRequiredStaticValue<'T> (moduleType: Type) (memberName: string) : Result<'T, string> =
  result {
    let! value =
      tryGetStaticMemberValue moduleType memberName
      |> Result.bind (
        ResultEx.requireSomeWith (fun () ->
          $"Compiled module '{moduleType.FullName}' does not define a static '{memberName}' value.")
      )

    match value with
    | :? 'T as typedValue -> return typedValue
    | null -> return! Error $"Compiled module '{moduleType.FullName}' defines '{memberName}', but it evaluates to null."
    | _ ->
      return!
        Error(
          $"Compiled module '{moduleType.FullName}' defines '{memberName}' with incompatible type '{value.GetType().FullName}'."
        )
  }

let private tryReadOptionalStaticValue<'T> (moduleType: Type) (memberName: string) : Result<'T option, string> =
  match tryGetStaticMemberValue moduleType memberName with
  | Error error -> Error error
  | Ok None -> Ok None
  | Ok(Some value) ->
    match value with
    | null -> Ok None
    | :? 'T as typedValue -> Ok(Some typedValue)
    | _ ->
      Error(
        $"Compiled module '{moduleType.FullName}' defines '{memberName}' with incompatible type '{value.GetType().FullName}'."
      )

let private tryFindModuleType (assembly: Assembly) (moduleName: string) =
  let exactMatch =
    assembly.GetTypes()
    |> Array.tryFind (fun candidate ->
      String.Equals(candidate.FullName, moduleName, StringComparison.Ordinal)
      || String.Equals(candidate.Name, moduleName, StringComparison.Ordinal))

  match exactMatch with
  | Some moduleType -> Ok moduleType
  | None ->
    let availableTypes =
      assembly.GetTypes()
      |> Array.map _.FullName
      |> Array.filter (String.IsNullOrWhiteSpace >> not)
      |> Array.sort
      |> String.concat ", "

    Error(
      $"Compiled module '{moduleName}' was not found in assembly '{assembly.FullName}'. Available types: {availableTypes}"
    )

let tryLoadGeneratedSchemaModuleFromAssembly
  (assembly: Assembly)
  (moduleName: string)
  : Result<GeneratedSchemaModule, string> =
  result {
    let! moduleType = tryFindModuleType assembly moduleName

    let! schema = tryReadRequiredStaticValue<SqlFile> moduleType "Schema"
    let! schemaIdentity = tryReadOptionalStaticValue<SchemaIdentity> moduleType "SchemaIdentity"
    let! schemaHash = tryReadOptionalStaticValue<string> moduleType "SchemaHash"
    let! dbApp = tryReadOptionalStaticValue<string> moduleType "DbApp"
    let! defaultDbInstance = tryReadOptionalStaticValue<string> moduleType "DefaultDbInstance"

    return
      { schema = schema
        schemaIdentity = schemaIdentity
        schemaHash = schemaHash
        dbApp = dbApp
        defaultDbInstance = defaultDbInstance }
  }

let resolveGeneratedModuleDbFileName (generatedModule: GeneratedSchemaModule) (instance: string option) =
  result {
    let! dbApp = generatedModule.dbApp |> ResultEx.requireSome "Compiled generated module does not define DbApp."
    let! schemaHash = generatedModule.schemaHash |> ResultEx.requireSome "Compiled generated module does not define SchemaHash."
    return! buildSchemaBoundDbFileName dbApp instance schemaHash
  }

let tryLoadGeneratedSchemaModuleFromAssemblyPath
  (assemblyPath: string)
  (moduleName: string)
  : Result<GeneratedSchemaModule, string> =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error "Compiled assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled assembly was not found: {fullAssemblyPath}"
    else
      try
        let assembly = Assembly.LoadFrom fullAssemblyPath
        tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName
      with ex ->
        Error $"Could not load compiled assembly '{fullAssemblyPath}': {ex.Message}"

let private resolveSchemaIdentity (generatedModule: GeneratedSchemaModule) =
  generatedModule.schemaIdentity
  |> Option.orElseWith (fun () ->
    generatedModule.schemaHash
    |> Option.map (fun schemaHash ->
      { schemaHash = schemaHash
        schemaCommit = None }))
  |> ResultEx.requireSome "Compiled generated module does not define SchemaIdentity or SchemaHash."

let private toSqliteError (message: string) = SqliteException(message, 0)

let private loadSchemaExecutionContext
  (loadGeneratedModule: unit -> Result<GeneratedSchemaModule, string>)
  : Task<Result<GeneratedSchemaModule * SchemaIdentity, SqliteException>> =
  Task.FromResult(
    result {
      let! generatedModule = loadGeneratedModule ()
      let! schemaIdentity = resolveSchemaIdentity generatedModule
      return generatedModule, schemaIdentity
    }
    |> Result.mapError toSqliteError
  )

let getMigratePlanFromAssembly
  (assembly: Assembly)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigratePlanReport, SqliteException>> =
  task {
    let! context = loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! getMigratePlanWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let getMigratePlanFromAssemblyPath
  (assemblyPath: string)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigratePlanReport, SqliteException>> =
  task {
    let! context =
      loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssemblyPath assemblyPath moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! getMigratePlanWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let runMigrateFromAssembly
  (assembly: Assembly)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    let! context = loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! runMigrateWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let runMigrateFromAssemblyPath
  (assemblyPath: string)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    let! context =
      loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssemblyPath assemblyPath moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! runMigrateWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let runOfflineMigrateFromAssembly
  (assembly: Assembly)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    let! context = loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! runOfflineMigrateWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let runOfflineMigrateFromAssemblyPath
  (assemblyPath: string)
  (moduleName: string)
  (oldDbPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    let! context =
      loadSchemaExecutionContext (fun () -> tryLoadGeneratedSchemaModuleFromAssemblyPath assemblyPath moduleName)

    match context with
    | Error error -> return Error error
    | Ok(generatedModule, schemaIdentity) ->
      return! runOfflineMigrateWithSchema oldDbPath schemaIdentity generatedModule.schema newDbPath
  }

let runInitFromAssembly
  (assembly: Assembly)
  (moduleName: string)
  (newDbPath: string)
  : Task<Result<InitResult, SqliteException>> =
  task {
    match tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName with
    | Error error -> return Error(toSqliteError error)
    | Ok generatedModule -> return! runInitWithSchema generatedModule.schema newDbPath
  }

let runInitFromAssemblyPath
  (assemblyPath: string)
  (moduleName: string)
  (newDbPath: string)
  : Task<Result<InitResult, SqliteException>> =
  task {
    match tryLoadGeneratedSchemaModuleFromAssemblyPath assemblyPath moduleName with
    | Error error -> return Error(toSqliteError error)
    | Ok generatedModule -> return! runInitWithSchema generatedModule.schema newDbPath
  }
