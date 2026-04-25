module MigLib.CompiledSchema

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Text.Json
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

let private formatLoaderExceptions (errors: exn array) =
  errors
  |> Array.choose (fun error ->
    if isNull error then
      None
    else
      let message =
        if String.IsNullOrWhiteSpace error.Message then
          "(no message)"
        else
          error.Message.Trim()

      Some $"- {error.GetType().FullName}: {message}")
  |> String.concat Environment.NewLine

let private formatAssemblyLoadError (ex: exn) =
  match ex with
  | :? ReflectionTypeLoadException as reflectionError ->
    let loaderDetails = formatLoaderExceptions reflectionError.LoaderExceptions

    if String.IsNullOrWhiteSpace loaderDetails then
      reflectionError.Message
    else
      $"{reflectionError.Message}{Environment.NewLine}{loaderDetails}"
  | _ -> ex.Message

let private tryGetAssemblyTypes (assembly: Assembly) =
  try
    Ok(assembly.GetTypes())
  with ex ->
    Error $"Could not enumerate types from assembly '{assembly.FullName}': {formatAssemblyLoadError ex}"

let private tryGetJsonProperty (name: string) (element: JsonElement) =
  let mutable value = Unchecked.defaultof<JsonElement>

  if element.TryGetProperty(name, &value) then Some value else None

let private defaultNugetPackagesRoot () =
  match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
  | null
  | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
  | value -> value

let private tryReadDepsAssemblyPaths (mainAssemblyPath: string) =
  let depsPath = Path.ChangeExtension(mainAssemblyPath, ".deps.json")

  if not (File.Exists depsPath) then
    Map.empty
  else
    let assemblyDirectory = Path.GetDirectoryName mainAssemblyPath
    let packagesRoot = defaultNugetPackagesRoot ()

    try
      use document = JsonDocument.Parse(File.ReadAllText depsPath)
      let root = document.RootElement

      match tryGetJsonProperty "targets" root with
      | None -> Map.empty
      | Some targets when targets.ValueKind <> JsonValueKind.Object -> Map.empty
      | Some targets ->
        let firstTarget = targets.EnumerateObject() |> Seq.tryHead

        match firstTarget with
        | None -> Map.empty
        | Some target ->
          target.Value.EnumerateObject()
          |> Seq.choose (fun library ->
            let libraryName = library.Name
            let slashIndex = libraryName.IndexOf '/'

            if slashIndex <= 0 then
              None
            else
              let packageName = libraryName.Substring(0, slashIndex)
              let packageVersion = libraryName.Substring(slashIndex + 1)

              library.Value
              |> tryGetJsonProperty "runtime"
              |> Option.filter (fun runtime -> runtime.ValueKind = JsonValueKind.Object)
              |> Option.map (fun runtime -> packageName, packageVersion, runtime))
          |> Seq.collect (fun (packageName, packageVersion, runtime) ->
            runtime.EnumerateObject()
            |> Seq.choose (fun runtimeAsset ->
              let assetRelativePath = runtimeAsset.Name

              if not (assetRelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) then
                None
              else
                let assetName = Path.GetFileNameWithoutExtension assetRelativePath
                let candidatePath =
                  if assetRelativePath.Contains('/') || assetRelativePath.Contains('\\') then
                    Path.Combine(packagesRoot, packageName.ToLowerInvariant(), packageVersion, assetRelativePath.Replace('/', Path.DirectorySeparatorChar))
                  else
                    Path.Combine(assemblyDirectory, assetRelativePath)

                Some(assetName, candidatePath)))
          |> Seq.fold (fun state (assetName, candidatePath) ->
            if File.Exists candidatePath then
              Map.add assetName candidatePath state
            else
              state) Map.empty
    with _ ->
      Map.empty

type private CompiledAssemblyLoadContext(mainAssemblyPath: string) as this =
  inherit AssemblyLoadContext($"CompiledSchema:{Path.GetFileNameWithoutExtension mainAssemblyPath}", isCollectible = true)

  let resolver = AssemblyDependencyResolver(mainAssemblyPath)
  let assemblyDirectory = Path.GetDirectoryName mainAssemblyPath
  let depsAssemblyPaths = tryReadDepsAssemblyPaths mainAssemblyPath

  override _.Load(assemblyName: AssemblyName) =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.tryFind (fun loaded -> AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), assemblyName))
    |> Option.defaultWith (fun () ->
      let resolvedPath = resolver.ResolveAssemblyToPath assemblyName

      if String.IsNullOrWhiteSpace resolvedPath then
        match depsAssemblyPaths.TryFind assemblyName.Name with
        | Some candidatePath when File.Exists candidatePath -> this.LoadFromAssemblyPath candidatePath
        | _ ->
          let candidatePath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll")

          if File.Exists candidatePath then
            this.LoadFromAssemblyPath candidatePath
          else
            null
      else
        this.LoadFromAssemblyPath resolvedPath)

let withAssemblyPathResolver
  (assemblyPath: string)
  (work: string -> AssemblyLoadContext -> Result<'a, string>)
  : Result<'a, string> =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error "Compiled assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled assembly was not found: {fullAssemblyPath}"
    else
      let loadContext = new CompiledAssemblyLoadContext(fullAssemblyPath)

      try
        work fullAssemblyPath loadContext
      finally
        loadContext.Unload()

let loadAssemblyFromPathWithResolver (assemblyPath: string) : Result<Assembly, string> =
  withAssemblyPathResolver assemblyPath (fun fullAssemblyPath loadContext ->
    try
      loadContext.LoadFromAssemblyPath fullAssemblyPath |> Ok
    with ex ->
      Error $"Could not load compiled assembly '{fullAssemblyPath}': {formatAssemblyLoadError ex}")

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
  tryGetAssemblyTypes assembly
  |> Result.bind (fun types ->
    match
      types
      |> Array.tryFind (fun candidate ->
        String.Equals(candidate.FullName, moduleName, StringComparison.Ordinal)
        || String.Equals(candidate.Name, moduleName, StringComparison.Ordinal))
    with
    | Some moduleType -> Ok moduleType
    | None ->
      let availableTypes =
        types
        |> Array.map _.FullName
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.sort
        |> String.concat ", "

      Error(
        $"Compiled module '{moduleName}' was not found in assembly '{assembly.FullName}'. Available types: {availableTypes}"
      ))

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
  withAssemblyPathResolver assemblyPath (fun fullAssemblyPath loadContext ->
    try
      let assembly = loadContext.LoadFromAssemblyPath fullAssemblyPath
      tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName
    with ex ->
      Error $"Could not load compiled assembly '{fullAssemblyPath}': {formatAssemblyLoadError ex}")

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
