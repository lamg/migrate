module internal MigLib.Codegen.Execution

open System
open System.IO
open System.Reflection
open System.Runtime.Loader

open MigLib.Schema.Types
open MigLib.Types
open MigLib.Codegen.Generation
open MigLib.Codegen.Inputs
open MigLib.TaskResult

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

let private tryFindModuleType (assembly: Assembly) (moduleName: string) =
  tryGetAssemblyTypes assembly
  |> Result.bind (fun types ->
    match
      types
      |> Array.tryFind (fun candidate -> String.Equals(candidate.FullName, moduleName, StringComparison.Ordinal))
    with
    | Some moduleType -> Ok moduleType
    | None -> Error $"Compiled schema module '{moduleName}' was not found in assembly '{assembly.FullName}'.")

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
          $"Compiled schema module '{moduleType.FullName}' does not define a static '{memberName}' value.")
      )

    match value with
    | :? 'T as typedValue -> return typedValue
    | null ->
      return! Error $"Compiled schema module '{moduleType.FullName}' defines '{memberName}', but it evaluates to null."
    | _ ->
      return!
        Error(
          $"Compiled schema module '{moduleType.FullName}' defines '{memberName}' with incompatible type '{value.GetType().FullName}'."
        )
  }

type private CodegenLoadContext(mainAssemblyPath: string) as this =
  inherit AssemblyLoadContext($"Codegen:{Path.GetFileNameWithoutExtension mainAssemblyPath}", isCollectible = true)

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

let private withAssemblyResolver assemblyPath work =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error "Compiled schema assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled schema assembly was not found: {fullAssemblyPath}"
    else
      let loadContext = new CodegenLoadContext(fullAssemblyPath)

      try
        work fullAssemblyPath loadContext
      finally
        loadContext.Unload()

let private loadSchema inputs =
  withAssemblyResolver inputs.schemaAssembly.assemblyPath (fun fullAssemblyPath loadContext ->
    try
      let assembly = loadContext.LoadFromAssemblyPath fullAssemblyPath

      result {
        let! moduleType = tryFindModuleType assembly inputs.schemaModuleName
        return! tryReadRequiredStaticValue<SqlFile> moduleType "Schema"
      }
    with ex ->
      Error $"Could not load compiled schema assembly '{fullAssemblyPath}': {formatAssemblyLoadError ex}")

let runCodegen (inputs: CodegenInputs) : Result<CodegenResult, MigError> =
  result {
    let! schema = loadSchema inputs |> Result.mapError MigError.Regular

    let! stats =
      generateCodeFromSchema inputs.generatedModuleName inputs.dbApp inputs.schemaSourcePath schema inputs.outputPath
      |> Result.mapError MigError.Regular

    return
      { outputPath = Path.GetFullPath inputs.outputPath
        generatedModuleName = inputs.generatedModuleName
        generatedFiles = stats.generatedFiles }
  }

let codegen (project: MigProject) : Result<CodegenResult, MigError> =
  result {
    let! inputs = resolveInputs project
    return! runCodegen inputs
  }
