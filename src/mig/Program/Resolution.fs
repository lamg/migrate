namespace Mig

open System
open System.IO
open System.Xml.Linq
open MigLib.Build
open MigLib.CompiledSchema
open MigLib.Db
open MigLib.Util
open ProgramCommon

module internal ProgramResolution =
  let defaultSchemaFsPathForCurrentDirectory (currentDirectory: string) =
    Path.Combine(currentDirectory, "Schema.fs")

  let resolveCommandDirectory (commandName: string) (candidate: string option) : Result<string, string> =
    let targetDirectory =
      candidate
      |> Option.defaultValue (Directory.GetCurrentDirectory())
      |> Path.GetFullPath

    if Directory.Exists targetDirectory then
      Ok targetDirectory
    else
      Error $"Directory does not exist for `{commandName}`: {targetDirectory}"

  let private isHexHashSegment (value: string) =
    value.Length = 16 && value |> Seq.forall Uri.IsHexDigit

  let private isDirectoryHashNamedSqlite (directoryName: string) (path: string) =
    if not (Path.GetExtension(path).Equals(".sqlite", StringComparison.OrdinalIgnoreCase)) then
      false
    else
      let fileStem = Path.GetFileNameWithoutExtension path
      let prefix = $"{directoryName}-"

      if fileStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
        let hashSegment = fileStem.Substring prefix.Length
        isHexHashSegment hashSegment
      else
        false

  let inferOldDbFromCurrentDirectory
    (currentDirectory: string)
    (directoryName: string)
    (excludePath: string option)
    : Result<string, string> =
    let shouldExclude (path: string) =
      match excludePath with
      | Some excludedPath -> path.Equals(excludedPath, StringComparison.OrdinalIgnoreCase)
      | None -> false

    let sqliteFiles =
      Directory.GetFiles(currentDirectory, "*.sqlite")
      |> Array.filter (fun path -> not (shouldExclude path))
      |> Array.sort

    let candidates =
      sqliteFiles |> Array.filter (isDirectoryHashNamedSqlite directoryName)

    if candidates.Length = 1 then
      Ok candidates[0]
    elif candidates.Length > 1 then
      let candidateList = String.concat ", " candidates

      Error
        $"Could not infer old database automatically. Found multiple candidates matching '{directoryName}-<old-hash>.sqlite': {candidateList}."
    elif sqliteFiles.Length > 0 then
      let discoveredList = String.concat ", " sqliteFiles

      Error
        $"Could not infer old database automatically. Found sqlite files that do not match '{directoryName}-<old-hash>.sqlite': {discoveredList}."
    else
      Error
        $"Could not infer old database automatically. Expected exactly one source matching '{directoryName}-<old-hash>.sqlite' in {currentDirectory}."

  let private getSchemaSourceCandidates (currentDirectory: string) =
    [ Path.Combine(currentDirectory, "Schema.fs") ]

  let private resolveSchemaBoundDbPathFromKnownSource
    (commandName: string)
    (currentDirectory: string)
    (schemaSourcePath: string)
    : Result<SchemaBoundDbPath, string> =
    match deriveSchemaBoundDbFileName schemaSourcePath with
    | Error message ->
      Error
        $"Could not infer new database automatically from schema source '{schemaSourcePath}' for `{commandName}`: {message}."
    | Ok dbFileName ->
      Ok
        { schemaSourcePath = schemaSourcePath
          path = Path.Combine(currentDirectory, dbFileName) }

  let resolveDefaultSchemaBoundDbPath
    (commandName: string)
    (currentDirectory: string)
    : Result<SchemaBoundDbPath, string> =
    let existingSchemaSource =
      getSchemaSourceCandidates currentDirectory |> List.tryFind File.Exists

    match existingSchemaSource with
    | Some schemaSourcePath -> resolveSchemaBoundDbPathFromKnownSource commandName currentDirectory schemaSourcePath
    | None ->
      let lookedFor =
        getSchemaSourceCandidates currentDirectory
        |> List.map Path.GetFullPath
        |> String.concat ", "

      Error
        $"Could not infer new database automatically for `{commandName}`. No schema source was found. Looked for: {lookedFor}."

  let resolveCodegenOutputPath
    (currentDirectory: string)
    (schemaSourceLabel: string)
    (defaultOutputFileName: string)
    (candidate: string option)
    : Result<string, string> =
    let outputFileName = candidate |> Option.defaultValue defaultOutputFileName

    if String.IsNullOrWhiteSpace outputFileName then
      Error "Output file name for `codegen` cannot be empty."
    elif Path.IsPathRooted outputFileName then
      Error "Output file for `codegen` must be a file name, not an absolute path."
    elif not (outputFileName.Equals(Path.GetFileName outputFileName, StringComparison.Ordinal)) then
      Error $"Output file for `codegen` must be in the same directory as {schemaSourceLabel} (no subdirectories)."
    else
      Ok(Path.Combine(currentDirectory, outputFileName))

  let private resolveCompiledModuleName (candidate: string option) : Result<string, string> =
    let moduleName = candidate |> Option.defaultValue "Db"

    if String.IsNullOrWhiteSpace moduleName then
      Error "Compiled module name cannot be empty."
    else
      Ok moduleName

  let private tryReadAssemblyNameFromProject (projectPath: string) : Result<string option, string> =
    try
      let document = XDocument.Load projectPath

      let assemblyName =
        document.Descendants()
        |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, "AssemblyName", StringComparison.Ordinal))
        |> Option.map _.Value
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

      Ok assemblyName
    with ex ->
      Error
        $"Could not read project file '{Path.GetFullPath projectPath}' while inferring the compiled assembly: {ex.Message}"

  let private resolveProjectOutputPath (projectPath: string) =
    result {
      let! assemblyName = tryReadAssemblyNameFromProject projectPath

      let assemblyFileName =
        assemblyName
        |> Option.defaultValue (Path.GetFileNameWithoutExtension projectPath)
        |> fun name -> $"{name}.dll"

      return Path.Combine(Path.GetDirectoryName projectPath, "bin", "Debug", "net10.0", assemblyFileName)
    }

  let private tryDiscoverSchemaAssemblyPath
    (commandName: string)
    (currentDirectory: string)
    : Result<string option, string> =
    let schemaProjectPath = Path.Combine(currentDirectory, "Schema.fsproj")

    if File.Exists schemaProjectPath then
      result {
        let! schemaAssemblyPath = resolveProjectOutputPath schemaProjectPath

        if File.Exists schemaAssemblyPath then
          return Some(Path.GetFullPath schemaAssemblyPath)
        else
          return!
            Error
              $"Could not infer compiled assembly automatically for `{commandName}`. Found 'Schema.fsproj' and expected build output at '{Path.GetFullPath schemaAssemblyPath}'. Build the schema project or pass --assembly explicitly."
      }
    else
      let projectFiles = Directory.GetFiles(currentDirectory, "*.fsproj") |> Array.sort

      match projectFiles with
      | [||] -> Ok None
      | [| projectPath |] ->
        let projectName = Path.GetFileNameWithoutExtension projectPath

        let inferredAssemblyPath =
          Path.Combine(currentDirectory, "bin", "Debug", "net10.0", $"{projectName}.dll")

        if File.Exists inferredAssemblyPath then
          Ok(Some(Path.GetFullPath inferredAssemblyPath))
        else
          Error
            $"Could not infer compiled assembly automatically for `{commandName}`. Found project '{Path.GetFileName projectPath}' but expected build output at '{Path.GetFullPath inferredAssemblyPath}'. Build the project or pass --assembly explicitly."
      | many ->
        let projectList = many |> Array.map Path.GetFileName |> String.concat ", "

        Error
          $"Could not infer compiled assembly automatically for `{commandName}`. Found multiple .fsproj files in {currentDirectory}: {projectList}. Pass --assembly explicitly."

  let private tryDiscoverRuntimeAssemblyPath
    (commandName: string)
    (currentDirectory: string)
    : Result<string option, string> =
    let projectFiles =
      Directory.GetFiles(currentDirectory, "*.fsproj")
      |> Array.filter (fun path ->
        not (String.Equals(Path.GetFileName path, "Schema.fsproj", StringComparison.OrdinalIgnoreCase)))
      |> Array.sort

    match projectFiles with
    | [||] ->
      if File.Exists(Path.Combine(currentDirectory, "Schema.fsproj")) then
        Error
          $"Could not infer compiled runtime assembly automatically for `{commandName}`. Found only 'Schema.fsproj' in {currentDirectory}. Runtime commands need the compiled generated Db module from the main application project, so pass --assembly explicitly."
      else
        Ok None
    | [| projectPath |] ->
      result {
        let! inferredAssemblyPath = resolveProjectOutputPath projectPath

        if File.Exists inferredAssemblyPath then
          return Some(Path.GetFullPath inferredAssemblyPath)
        else
          return!
            Error
              $"Could not infer compiled runtime assembly automatically for `{commandName}`. Found project '{Path.GetFileName projectPath}' but expected build output at '{Path.GetFullPath inferredAssemblyPath}'. Build the project or pass --assembly explicitly."
      }
    | many ->
      let projectList = many |> Array.map Path.GetFileName |> String.concat ", "

      Error
        $"Could not infer compiled runtime assembly automatically for `{commandName}`. Found multiple non-schema .fsproj files in {currentDirectory}: {projectList}. Pass --assembly explicitly."

  let private resolveCompiledMode
    (assemblyPath: string option)
    (moduleName: string option)
    : Result<(string * string) option, string> =
    match assemblyPath with
    | Some assemblyPath ->
      match resolveCompiledModuleName moduleName with
      | Ok resolvedModuleName -> Ok(Some(assemblyPath, resolvedModuleName))
      | Error message -> Error message
    | None ->
      match moduleName with
      | Some _ -> Error "--module requires --assembly."
      | None -> Ok None

  let resolveRequiredCompiledMode
    (commandName: string)
    (currentDirectory: string)
    (assemblyPath: string option)
    (moduleName: string option)
    : Result<string * string, string> =
    result {
      let! resolvedModuleName = resolveCompiledModuleName moduleName

      match assemblyPath with
      | Some explicitAssemblyPath -> return explicitAssemblyPath, resolvedModuleName
      | None ->
        let! discoveredAssemblyPath = tryDiscoverRuntimeAssemblyPath commandName currentDirectory

        match discoveredAssemblyPath with
        | Some inferredAssemblyPath -> return inferredAssemblyPath, resolvedModuleName
        | None ->
          return!
            Error
              $"`{commandName}` requires --assembly pointing to a compiled generated Db module. No .fsproj was found in {currentDirectory}. Use --module to override the default module name `Db`."
    }

  let private resolveCodegenGeneratedModuleName (candidate: string option) : Result<string, string> =
    let defaultModuleName = "Db"
    let moduleName = candidate |> Option.defaultValue defaultModuleName

    if String.IsNullOrWhiteSpace moduleName then
      Error "codegen failed: module name cannot be empty."
    else
      Ok moduleName

  let private resolveRequiredCodegenCompiledInput
    (currentDirectory: string)
    (assemblyPath: string option)
    (schemaModuleName: string option)
    : Result<string * string, string> =
    result {
      let resolvedSchemaModuleName = schemaModuleName |> Option.defaultValue "Schema"

      if String.IsNullOrWhiteSpace resolvedSchemaModuleName then
        return! Error "codegen failed: compiled schema module name cannot be empty."
      else
        match assemblyPath with
        | Some explicitAssemblyPath -> return explicitAssemblyPath, resolvedSchemaModuleName
        | None ->
          let! discoveredAssemblyPath = tryDiscoverSchemaAssemblyPath "codegen" currentDirectory

          match discoveredAssemblyPath with
          | Some inferredAssemblyPath -> return inferredAssemblyPath, resolvedSchemaModuleName
          | None ->
            return!
              Error
                $"codegen failed: `codegen` requires --assembly pointing to compiled schema types. No .fsproj was found in {currentDirectory}. Use --schema-module to override the default schema module name `Schema`."
    }

  let resolveCodegenInputs
    (currentDirectory: string)
    (assemblyPath: string option)
    (schemaModuleName: string option)
    (generatedModuleName: string option)
    (outputPath: string option)
    : Result<string * string * string * string, string> =
    result {
      let! assemblyPath, schemaModuleName =
        resolveRequiredCodegenCompiledInput currentDirectory assemblyPath schemaModuleName

      let schemaPath = defaultSchemaFsPathForCurrentDirectory currentDirectory

      do!
        if File.Exists schemaPath then
          Ok()
        else
          Error $"Schema source file was not found: {schemaPath}"

      let! generatedModuleName = resolveCodegenGeneratedModuleName generatedModuleName

      let! outputPath = resolveCodegenOutputPath currentDirectory "Schema.fs" "Db.fs" outputPath

      return assemblyPath, schemaModuleName, generatedModuleName, outputPath
    }

  let resolveCompiledModuleForCommand
    (commandName: string)
    (currentDirectory: string)
    (assemblyPath: string)
    (moduleName: string)
    : Result<ResolvedCompiledModule, string> =
    result {
      let fullAssemblyPath = Path.GetFullPath assemblyPath

      let! generatedModule =
        tryLoadGeneratedSchemaModuleFromAssemblyPath assemblyPath moduleName
        |> Result.mapError (fun message ->
          $"Could not load compiled generated module '{moduleName}' from '{assemblyPath}' for `{commandName}`: {message}")

      let! dbFileName =
        generatedModule.dbFile
        |> ResultEx.requireSomeWith (fun () ->
          $"Compiled generated module '{moduleName}' from '{assemblyPath}' does not define DbFile for `{commandName}`.")

      let! dbPath =
        resolveDatabaseFilePath currentDirectory dbFileName
        |> Result.mapError (fun message -> $"Could not resolve DbFile '{dbFileName}' for `{commandName}`: {message}")

      return
        { assemblyPath = fullAssemblyPath
          moduleName = moduleName
          generatedModule = generatedModule
          newDbPath = dbPath }
    }

  let resolveRequiredCompiledModuleForCommand
    (commandName: string)
    (currentDirectory: string)
    (assemblyPath: string option)
    (moduleName: string option)
    : Result<ResolvedCompiledModule, string> =
    result {
      let! assemblyPath, moduleName = resolveRequiredCompiledMode commandName currentDirectory assemblyPath moduleName
      return! resolveCompiledModuleForCommand commandName currentDirectory assemblyPath moduleName
    }

  let printCompiledModuleInfo (compiledModule: ResolvedCompiledModule) =
    printfn $"Compiled assembly: {compiledModule.assemblyPath}"
    printfn $"Compiled module: {compiledModule.moduleName}"

    match compiledModule.generatedModule.schemaHash with
    | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
    | None -> ()

  let private resolveTargetDbPathForCommand
    (commandName: string)
    (currentDirectory: string)
    (compiledMode: Result<(string * string) option, string>)
    : Result<string, string> =
    match compiledMode with
    | Error message -> Error message
    | Ok(Some(assemblyPath, moduleName)) ->
      resolveCompiledModuleForCommand commandName currentDirectory assemblyPath moduleName
      |> Result.map _.newDbPath
    | Ok None ->
      resolveDefaultSchemaBoundDbPath commandName currentDirectory
      |> Result.map _.path

  let inferOldDbWithExcludedTarget
    (currentDirectory: string)
    (directoryName: string)
    (newDb: string)
    : Result<string, string> =
    inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)
    |> Result.mapError (fun message ->
      $"{message} Excluding target '{newDb}'. Use `-d` to select a different directory.")

  let resolveMigrationSourceDb
    (currentDirectory: string)
    (directoryName: string)
    (newDb: string)
    : Result<string option, string> =
    match inferOldDbWithExcludedTarget currentDirectory directoryName newDb with
    | Ok old -> Ok(Some old)
    | Error _ when File.Exists newDb -> Ok None
    | Error message -> Error message

  let private resolveOptionalTargetDbPathForCommand
    (commandName: string)
    (currentDirectory: string)
    (compiledMode: (string * string) option)
    : Result<string option, string> =
    result {
      match compiledMode with
      | Some _ ->
        let! inferredTarget = resolveTargetDbPathForCommand commandName currentDirectory (Ok compiledMode)
        return Some inferredTarget
      | None ->
        match resolveDefaultSchemaBoundDbPath commandName currentDirectory with
        | Ok inferredTarget -> return Some inferredTarget.path
        | Error _ -> return None
    }

  let resolveCompiledModeTargetDbPathForCommand
    (commandName: string)
    (currentDirectory: string)
    (assemblyPath: string option)
    (moduleName: string option)
    : Result<string, string> =
    resolveTargetDbPathForCommand commandName currentDirectory (resolveCompiledMode assemblyPath moduleName)

  let resolveOptionalCompiledModeTargetDbPathForCommand
    (commandName: string)
    (currentDirectory: string)
    (assemblyPath: string option)
    (moduleName: string option)
    : Result<string option, string> =
    result {
      let! compiledMode = resolveCompiledMode assemblyPath moduleName
      return! resolveOptionalTargetDbPathForCommand commandName currentDirectory compiledMode
    }
