module internal MigLib.Resolution.GeneratedSchema

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Xml.Linq

open MigLib.Schema.Types
open MigLib.Types
open MigLib.Resolution.Types
open MigLib.TaskResult

let private staticBindingFlags =
  BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

let private regularError message = Error(MigError.Regular message)

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

let private tryLoadProjectDocument (projectPath: string) : Result<XDocument, MigError> =
  try
    if String.IsNullOrWhiteSpace projectPath then
      regularError "Runtime project path is empty."
    elif not (File.Exists projectPath) then
      regularError $"Runtime project file was not found: {Path.GetFullPath projectPath}"
    else
      Ok(XDocument.Load projectPath)
  with ex ->
    regularError $"Could not read runtime project file '{Path.GetFullPath projectPath}': {ex.Message}"

let private tryReadProperty (name: string) (document: XDocument) =
  document.Descendants()
  |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, name, StringComparison.Ordinal))
  |> Option.map _.Value
  |> Option.map _.Trim()
  |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private tryGetAssemblyTypes (assembly: Assembly) =
  try
    Ok(assembly.GetTypes())
  with ex ->
    Error(
      MigError.Regular $"Could not enumerate types from assembly '{assembly.FullName}': {formatAssemblyLoadError ex}"
    )

let private tryFindModuleType (assembly: Assembly) (moduleName: string) =
  tryGetAssemblyTypes assembly
  |> Result.bind (fun types ->
    match
      types
      |> Array.tryFind (fun candidate -> String.Equals(candidate.FullName, moduleName, StringComparison.Ordinal))
    with
    | Some moduleType -> Ok moduleType
    | None ->
      Error(MigError.Regular $"Compiled module '{moduleName}' was not found in assembly '{assembly.FullName}'."))

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

let private tryReadRequiredStaticValue<'T> (moduleType: Type) (memberName: string) : Result<'T, MigError> =
  result {
    let! value =
      tryGetStaticMemberValue moduleType memberName
      |> Result.bind (
        ResultEx.requireSomeWith (fun () ->
          MigError.Regular $"Compiled module '{moduleType.FullName}' does not define a static '{memberName}' value.")
      )

    match value with
    | :? 'T as typedValue -> return typedValue
    | null ->
      return!
        Error(
          MigError.Regular $"Compiled module '{moduleType.FullName}' defines '{memberName}', but it evaluates to null."
        )
    | _ ->
      return!
        Error(
          MigError.Regular
            $"Compiled module '{moduleType.FullName}' defines '{memberName}' with incompatible type '{value.GetType().FullName}'."
        )
  }

type private GeneratedSchemaLoadContext(mainAssemblyPath: string) as this =
  inherit
    AssemblyLoadContext($"GeneratedSchema:{Path.GetFileNameWithoutExtension mainAssemblyPath}", isCollectible = true)

  let resolver = AssemblyDependencyResolver(mainAssemblyPath)
  let assemblyDirectory = Path.GetDirectoryName mainAssemblyPath

  override _.Load(assemblyName: AssemblyName) =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.tryFind (fun loaded -> AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), assemblyName))
    |> Option.defaultWith (fun () ->
      let resolvedPath = resolver.ResolveAssemblyToPath assemblyName

      if String.IsNullOrWhiteSpace resolvedPath then
        let candidatePath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll")

        if File.Exists candidatePath then
          this.LoadFromAssemblyPath candidatePath
        else
          null
      else
        this.LoadFromAssemblyPath resolvedPath)

let private withAssemblyResolver (assemblyPath: string) (work: string -> AssemblyLoadContext -> Result<'a, MigError>) =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error(MigError.Regular "Compiled assembly path is empty.")
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error(MigError.Regular $"Compiled assembly was not found: {fullAssemblyPath}")
    else
      let loadContext = new GeneratedSchemaLoadContext(fullAssemblyPath)

      try
        work fullAssemblyPath loadContext
      finally
        loadContext.Unload()

let private loadGeneratedModule (assemblyPath: string) (moduleName: string) =
  withAssemblyResolver assemblyPath (fun fullAssemblyPath loadContext ->
    try
      let assembly = loadContext.LoadFromAssemblyPath fullAssemblyPath

      result {
        let! moduleType = tryFindModuleType assembly moduleName
        let! schema = tryReadRequiredStaticValue<SqlFile> moduleType "Schema"
        let! schemaIdentity = tryReadRequiredStaticValue<SchemaIdentity> moduleType "SchemaIdentity"
        let! schemaHash = tryReadRequiredStaticValue<string> moduleType "SchemaHash"
        let! dbApp = tryReadRequiredStaticValue<string> moduleType "DbApp"
        let! defaultDbInstance = tryReadRequiredStaticValue<string> moduleType "DefaultDbInstance"

        return
          { schema = schema
            schemaIdentity = schemaIdentity
            schemaHash = schemaHash
            dbApp = dbApp
            defaultDbInstance = defaultDbInstance }
      }
    with ex ->
      Error(MigError.Regular $"Could not load compiled assembly '{fullAssemblyPath}': {formatAssemblyLoadError ex}"))

let resolveSchemaModuleName (assembly: ResolvedAssembly) : Result<string, MigError> =
  result {
    let! document = tryLoadProjectDocument assembly.project.runtimeProjectPath

    let! rootNamespace =
      document
      |> tryReadProperty "RootNamespace"
      |> ResultEx.requireSomeWith (fun () ->
        MigError.Regular
          $"Runtime project '{Path.GetFullPath assembly.project.runtimeProjectPath}' must define <RootNamespace> so generated module '<RootNamespace>.Db' can be resolved.")

    return $"{rootNamespace}.Db"
  }

let resolveGeneratedSchema (assembly: ResolvedAssembly) : Result<ResolvedGeneratedSchema, MigError> =
  result {
    let! moduleName = resolveSchemaModuleName assembly

    let! generatedModule = loadGeneratedModule assembly.assemblyPath moduleName

    return
      { assembly = assembly
        moduleName = moduleName
        generatedModule = generatedModule }
  }
