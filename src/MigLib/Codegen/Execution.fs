module internal MigLib.Codegen.Execution

open System
open System.IO
open System.Reflection
open System.Runtime.Loader

open MigLib.Types
open MigLib.Codegen.Generation
open MigLib.Codegen.Inputs
open MigLib.Dsl.Attributes
open MigLib.Resolution.SchemaReflection
open MigLib.Resolution.SchemaReflection.Naming
open MigLib.TaskResult

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

let private tryGetGeneratedDbNamespaceAttribute (candidate: Type) =
  let attribute = candidate.GetCustomAttribute<GeneratedDbNamespaceAttribute>()

  if isNull (box attribute) then None else Some attribute

let private tryResolveAttributedSchemaModules (assembly: Assembly) =
  result {
    let! types = tryGetAssemblyTypes assembly

    let candidates =
      types
      |> Array.choose (fun candidate ->
        tryGetGeneratedDbNamespaceAttribute candidate
        |> Option.map (fun attribute -> candidate, attribute.NamespaceName.Trim()))
      |> Array.sortBy (fun (candidate, _) -> candidate.FullName)

    match candidates with
    | [||] ->
      return!
        Error
          $"Compiled MigSchema assembly '{assembly.FullName}' does not contain a module marked with GeneratedDbNamespaceAttribute."
    | many when
      many
      |> Array.exists (fun (_, generatedDbNamespace) -> String.IsNullOrWhiteSpace generatedDbNamespace)
      ->
      let moduleList =
        many
        |> Array.filter (fun (_, generatedDbNamespace) -> String.IsNullOrWhiteSpace generatedDbNamespace)
        |> Array.map (fst >> _.FullName)
        |> String.concat ", "

      return! Error $"Compiled schema modules have empty GeneratedDbNamespace values: {moduleList}."
    | many ->
      let namespaces = many |> Array.map snd |> Array.distinct

      if namespaces.Length <> 1 then
        let moduleList =
          many
          |> Array.map (fun (moduleType, generatedDbNamespace) -> $"{moduleType.FullName}={generatedDbNamespace}")
          |> String.concat ", "

        return!
          Error
            $"Compiled MigSchema assembly '{assembly.FullName}' contains schema modules with different GeneratedDbNamespaceAttribute values: {moduleList}."
      else
        return many |> Array.map fst |> Array.toList, namespaces[0]
  }

let private tryFindSchemaTypes (assembly: Assembly) (assemblyTypes: Type array) (moduleTypes: Type list) =
  let moduleNames = moduleTypes |> List.map _.FullName

  assemblyTypes
  |> Array.filter (fun t -> t.Assembly = assembly)
  |> Array.filter (fun t ->
    moduleNames
    |> List.exists (fun moduleName -> Seed.isTypeUnderModuleName moduleName t))
  |> Array.filter (fun t -> isRecordType t || isUnionType t)
  |> Array.sortBy _.FullName
  |> Array.toList

let private requireSchemaTypes (moduleTypes: Type list) (schemaTypes: Type list) =
  if schemaTypes.IsEmpty then
    let moduleList = moduleTypes |> List.map _.FullName |> String.concat ", "

    Error $"No record or union schema types were found under compiled modules: {moduleList}."
  else
    Ok schemaTypes

let private ensureSchemaModulesHaveTypes (moduleTypes: Type list) (schemaTypes: Type list) =
  moduleTypes
  |> List.filter (fun moduleType ->
    schemaTypes
    |> List.exists (fun schemaType -> Seed.isTypeUnderModuleName moduleType.FullName schemaType)
    |> not)
  |> function
    | [] -> Ok()
    | missing ->
      let moduleList = missing |> List.map _.FullName |> String.concat ", "

      Error
        $"Compiled schema modules marked with GeneratedDbNamespaceAttribute did not contain record or union schema types: {moduleList}."

let private schemaReflectionErrorToString =
  function
  | MigError.Regular message -> message
  | MigError.Sqlite ex -> ex.Message
  | MigError.Other ex -> ex.Message

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
    Error "Compiled MigSchema assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled MigSchema assembly was not found: {fullAssemblyPath}"
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
        let! moduleTypes, generatedDbNamespace = tryResolveAttributedSchemaModules assembly

        let! assemblyTypes = tryGetAssemblyTypes assembly

        let! schema =
          let schemaTypes = tryFindSchemaTypes assembly assemblyTypes moduleTypes

          result {
            let! schemaTypes = requireSchemaTypes moduleTypes schemaTypes
            do! ensureSchemaModulesHaveTypes moduleTypes schemaTypes

            let moduleNames = moduleTypes |> List.map _.FullName

            return!
              Seed.buildSchemaFromAssemblyModuleTypes assembly moduleNames schemaTypes
              |> Result.mapError schemaReflectionErrorToString
          }

        return schema, generatedDbNamespace
      }
    with ex ->
      Error $"Could not load compiled MigSchema assembly '{fullAssemblyPath}': {formatAssemblyLoadError ex}")

let runCodegen (inputs: CodegenInputs) : Result<CodegenResult, MigError> =
  result {
    let! schema, generatedDbNamespace =
      loadSchema inputs |> Result.mapError MigError.Regular

    let generatedModuleName = $"{generatedDbNamespace}.Db"

    let! stats =
      generateCodeFromSchema generatedModuleName generatedDbNamespace schema inputs.outputPath
      |> Result.mapError MigError.Regular

    return
      {
        outputPath = Path.GetFullPath inputs.outputPath
        generatedModuleName = generatedModuleName
        generatedFiles = stats.generatedFiles
      }
  }

let codegen (projectDir: string) : Result<CodegenResult, MigError> =
  result {
    let! inputs = resolveInputs projectDir
    return! runCodegen inputs
  }
