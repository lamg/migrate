module Mig.Program

open Argu
open System
open System.IO
open System.Xml.Linq
open MigLib.Build
open MigLib.CompiledSchema
open MigLib.Db
open MigLib.Util
open Mig.CodeGen.CodeGen
open Mig.HotMigration

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type OfflineArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type InitArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ ->
        "directory that contains the target sqlite location for the generated Db module (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type PlanArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type DrainArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CutoverArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type ArchiveOldArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type ResetArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string
  | Dry_Run

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"
      | Dry_Run -> "print reset impact without dropping old migration tables or deleting the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type StatusArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-m")>] Module of name: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains a generated Db-style module"
      | Module _ -> "compiled module name when using --assembly (default: Db)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CodegenArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-a")>] Assembly of path: string
  | [<AltCommandLine("-s")>] Schema_Module of name: string
  | [<AltCommandLine("-m")>] Module of name: string
  | [<AltCommandLine("-o")>] Output of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains Schema.fs and generated source files (default: current directory)"
      | Assembly _ -> "compiled assembly that contains schema types for generated Db.fs code"
      | Schema_Module _ -> "compiled schema module name when using --assembly (default: Schema)"
      | Module _ -> "module name for generated F# code (default: Db)"
      | Output _ -> "output file name in the schema directory (default: Db.fs)"

type Command =
  | [<AltCommandLine("-v")>] Version
  | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
  | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
  | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
  | [<CliPrefix(CliPrefix.None)>] Offline of ParseResults<OfflineArgs>
  | [<CliPrefix(CliPrefix.None)>] Plan of ParseResults<PlanArgs>
  | [<CliPrefix(CliPrefix.None)>] Drain of ParseResults<DrainArgs>
  | [<CliPrefix(CliPrefix.None)>] Cutover of ParseResults<CutoverArgs>
  | [<CliPrefix(CliPrefix.None); CustomCommandLine("archive-old")>] ArchiveOld of ParseResults<ArchiveOldArgs>
  | [<CliPrefix(CliPrefix.None)>] Reset of ParseResults<ResetArgs>
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Version -> "print mig version"
      | Init _ -> "initialize a schema-matched database from a compiled generated module"
      | Codegen _ -> "generate Db.fs and query helpers from Schema.fs plus compiled schema types"
      | Migrate _ -> "create new database and copy data from old using a compiled generated module"
      | Offline _ -> "run a one-shot offline migration using a compiled generated module"
      | Plan _ -> "show a dry-run migration plan using a compiled generated module"
      | Drain _ -> "stop writes on old database and replay accumulated changes"
      | Cutover _ -> "mark new database as ready for serving"
      | ArchiveOld _ -> "archive the old database after cutover"
      | Reset _ -> "reset failed migration artifacts on old/new databases"
      | Status _ -> "show current migration state"

let private getVersionText () =
  let version = typeof<Command>.Assembly.GetName().Version

  if isNull version then
    "unknown"
  else
    $"{version.Major}.{version.Minor}.{version.Build}"

let private formatExceptionDetails (ex: exn) =
  let rec loop (current: exn) (acc: string list) =
    if isNull current then
      List.rev acc
    else
      let message =
        if String.IsNullOrWhiteSpace current.Message then
          "(no message)"
        else
          current.Message.Trim()

      let rendered = $"{current.GetType().FullName}: {message}"
      loop current.InnerException (rendered :: acc)

  let chain = loop ex [] |> String.concat " --> "
  let debugValue = Environment.GetEnvironmentVariable "MIG_DEBUG"

  if
    String.Equals(debugValue, "1", StringComparison.Ordinal)
    || String.Equals(debugValue, "true", StringComparison.OrdinalIgnoreCase)
  then
    $"{chain}{Environment.NewLine}{ex}"
  else
    chain

type private ResolvedCompiledModule =
  { assemblyPath: string
    moduleName: string
    generatedModule: GeneratedSchemaModule
    newDbPath: string }

let private defaultSchemaFsPathForCurrentDirectory (currentDirectory: string) =
  Path.Combine(currentDirectory, "Schema.fs")

let private resolveCommandDirectory (commandName: string) (candidate: string option) : Result<string, string> =
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

let private inferOldDbFromCurrentDirectory
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

type private SchemaBoundDbPath =
  { schemaSourcePath: string
    path: string }

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

let private resolveDefaultSchemaBoundDbPath
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

let private printMigrateRecoveryGuidance (oldDbPath: string) (newDbPath: string) =
  let oldSnapshot = getOldDatabaseStatus oldDbPath |> fun t -> t.Result
  let newDbPresent = File.Exists newDbPath

  let newSnapshot =
    if newDbPresent then
      Some(getNewDatabaseStatus newDbPath |> fun t -> t.Result)
    else
      None

  eprintfn "Recovery snapshot:"

  match oldSnapshot with
  | Ok report ->
    let oldMarkerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"

    let oldMigrationLogState =
      if report.migrationLogTablePresent then
        $"present ({report.migrationLogEntries} entries)"
      else
        "absent"

    eprintfn $"  Old marker status: {oldMarkerStatus}"
    eprintfn $"  Old _migration_log: {oldMigrationLogState}"
  | Error ex -> eprintfn $"  Old database snapshot unavailable: {formatExceptionDetails ex}"

  let newDbState = if newDbPresent then "present" else "absent"
  eprintfn $"  New database file: {newDbState} ({newDbPath})"

  match newSnapshot with
  | Some(Ok report) ->
    let newStatus = report.newMigrationStatus |> Option.defaultValue "no status marker"

    let idMappingState =
      if report.idMappingTablePresent then
        $"present ({report.idMappingEntries} entries)"
      else
        "absent"

    let migrationProgressState =
      if report.migrationProgressTablePresent then
        "present"
      else
        "absent"

    eprintfn $"  New migration status: {newStatus}"
    eprintfn $"  New _id_mapping: {idMappingState}"
    eprintfn $"  New _migration_progress: {migrationProgressState}"
  | Some(Error ex) -> eprintfn $"  New database snapshot unavailable: {formatExceptionDetails ex}"
  | None -> ()

  let hasRecordingMarker =
    match oldSnapshot with
    | Ok report ->
      report.oldMarkerStatus
      |> Option.exists (fun status -> status.Equals("recording", StringComparison.OrdinalIgnoreCase))
    | Error _ -> false

  let hasOldMigrationLog =
    match oldSnapshot with
    | Ok report -> report.migrationLogTablePresent
    | Error _ -> false

  let safeImmediateRerun =
    not newDbPresent && not hasRecordingMarker && not hasOldMigrationLog

  let guidance = ResizeArray<string>()
  guidance.Add "Keep the old database as source of truth; do not run drain/cutover after a failed migrate."

  if hasRecordingMarker then
    guidance.Add "Old marker is recording; new writes may be accumulating in _migration_log."

  if hasOldMigrationLog || hasRecordingMarker then
    guidance.Add "If restarting from scratch: stop writes first, then clear _migration_marker and _migration_log."

  if newDbPresent then
    guidance.Add $"Delete failed target database before rerun: {newDbPath}."
  else
    guidance.Add "No target database file was created."

  guidance.Add "Run `mig plan` to confirm inferred paths and preflight status."

  if safeImmediateRerun then
    guidance.Add "Current snapshot indicates immediate rerun is safe."
  else
    guidance.Add "Rerun `mig migrate` only after cleanup/reset conditions above are satisfied."

  eprintfn "Recovery guidance:"

  guidance |> Seq.iteri (fun index line -> eprintfn $"  {index + 1}. {line}")

let private resolveCodegenOutputPath
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

let private tryDiscoverProjectAssemblyPath
  (commandName: string)
  (currentDirectory: string)
  : Result<string option, string> =
  let schemaProjectPath = Path.Combine(currentDirectory, "Schema.fsproj")

  if File.Exists schemaProjectPath then
    result {
      let! schemaAssemblyName = tryReadAssemblyNameFromProject schemaProjectPath

      let schemaAssemblyFileName =
        schemaAssemblyName
        |> Option.defaultValue "Schema"
        |> fun assemblyName -> $"{assemblyName}.dll"

      let schemaAssemblyPath =
        Path.Combine(currentDirectory, "bin", "Debug", "net10.0", schemaAssemblyFileName)

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

let private resolveRequiredCompiledMode
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
      let! discoveredAssemblyPath = tryDiscoverProjectAssemblyPath commandName currentDirectory

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

let private resolveCodegenCompiledInput
  (assemblyPath: string option)
  (schemaModuleName: string option)
  : Result<(string * string) option, string> =
  match assemblyPath with
  | Some assemblyPath ->
    let resolvedSchemaModuleName = schemaModuleName |> Option.defaultValue "Schema"

    if String.IsNullOrWhiteSpace resolvedSchemaModuleName then
      Error "codegen failed: compiled schema module name cannot be empty."
    else
      Ok(Some(assemblyPath, resolvedSchemaModuleName))
  | None ->
    match schemaModuleName with
    | Some _ -> Error "codegen failed: --schema-module requires --assembly."
    | None -> Ok None

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
        let! discoveredAssemblyPath = tryDiscoverProjectAssemblyPath "codegen" currentDirectory

        match discoveredAssemblyPath with
        | Some inferredAssemblyPath -> return inferredAssemblyPath, resolvedSchemaModuleName
        | None ->
          return!
            Error
              $"codegen failed: `codegen` requires --assembly pointing to compiled schema types. No .fsproj was found in {currentDirectory}. Use --schema-module to override the default schema module name `Schema`."
  }

let private resolveCompiledModuleForCommand
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

let private resolveRequiredCompiledModuleForCommand
  (commandName: string)
  (currentDirectory: string)
  (assemblyPath: string option)
  (moduleName: string option)
  : Result<ResolvedCompiledModule, string> =
  result {
    let! assemblyPath, moduleName = resolveRequiredCompiledMode commandName currentDirectory assemblyPath moduleName
    return! resolveCompiledModuleForCommand commandName currentDirectory assemblyPath moduleName
  }

let private printCompiledModuleInfo (compiledModule: ResolvedCompiledModule) =
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

let private inferOldDbWithExcludedTarget
  (currentDirectory: string)
  (directoryName: string)
  (newDb: string)
  : Result<string, string> =
  inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)
  |> Result.mapError (fun message -> $"{message} Excluding target '{newDb}'. Use `-d` to select a different directory.")

let private resolveMigrationSourceDb
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

let private resolveCompiledModeTargetDbPathForCommand
  (commandName: string)
  (currentDirectory: string)
  (assemblyPath: string option)
  (moduleName: string option)
  : Result<string, string> =
  resolveTargetDbPathForCommand commandName currentDirectory (resolveCompiledMode assemblyPath moduleName)

let private resolveOptionalCompiledModeTargetDbPathForCommand
  (commandName: string)
  (currentDirectory: string)
  (assemblyPath: string option)
  (moduleName: string option)
  : Result<string option, string> =
  result {
    let! compiledMode = resolveCompiledMode assemblyPath moduleName
    return! resolveOptionalTargetDbPathForCommand commandName currentDirectory compiledMode
  }

let private finishCommand (commandName: string) (result: Result<int, string>) =
  match result with
  | Ok exitCode -> exitCode
  | Error message ->
    eprintfn $"{commandName} failed: {message}"
    1

let codegen (args: ParseResults<CodegenArgs>) =
  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "codegen" (args.TryGetResult CodegenArgs.Dir)

      let! assemblyPath, schemaModuleName =
        resolveRequiredCodegenCompiledInput
          currentDirectory
          (args.TryGetResult CodegenArgs.Assembly)
          (args.TryGetResult CodegenArgs.Schema_Module)

      let schemaPath = defaultSchemaFsPathForCurrentDirectory currentDirectory

      do!
        if File.Exists schemaPath then
          Ok()
        else
          Error $"Schema source file was not found: {schemaPath}"

      let! generatedModuleName = resolveCodegenGeneratedModuleName (args.TryGetResult CodegenArgs.Module)

      let! outputPath =
        resolveCodegenOutputPath currentDirectory "Schema.fs" "Db.fs" (args.TryGetResult CodegenArgs.Output)

      let! report =
        runCodegenFromAssemblyModulePath generatedModuleName schemaPath assemblyPath schemaModuleName outputPath

      writeCodegenReport (printfn "%s") report
      return 0
    }

  finishCommand "codegen" result

let migrate (args: ParseResults<MigrateArgs>) =
  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir)
      let directoryName = DirectoryInfo(currentDirectory).Name

      let! compiledModule =
        resolveRequiredCompiledModuleForCommand
          "migrate"
          currentDirectory
          (args.TryGetResult MigrateArgs.Assembly)
          (args.TryGetResult MigrateArgs.Module)

      let newDb = compiledModule.newDbPath
      let! sourceDb = resolveMigrationSourceDb currentDirectory directoryName newDb

      match sourceDb with
      | None ->
        printfn "Migrate skipped."
        printCompiledModuleInfo compiledModule
        printfn $"Database already present for current schema: {newDb}"
        return 0
      | Some old ->
        let! migrateResult =
          runMigrateFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
          |> fun t -> t.Result
          |> Result.mapError formatExceptionDetails

        printfn "Migrate complete."
        printfn $"Old database: {old}"
        printCompiledModuleInfo compiledModule
        printfn $"New database: {migrateResult.newDbPath}"
        printfn $"Copied tables: {migrateResult.copiedTables}"
        printfn $"Copied rows: {migrateResult.copiedRows}"
        return 0
    }

  match result with
  | Error message ->
    let currentDirectoryResult =
      resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir)

    match currentDirectoryResult with
    | Ok currentDirectory ->
      let directoryName = DirectoryInfo(currentDirectory).Name

      match
        resolveRequiredCompiledModuleForCommand
          "migrate"
          currentDirectory
          (args.TryGetResult MigrateArgs.Assembly)
          (args.TryGetResult MigrateArgs.Module)
      with
      | Ok compiledModule ->
        let newDb = compiledModule.newDbPath

        match resolveMigrationSourceDb currentDirectory directoryName newDb with
        | Ok(Some old) ->
          printMigrateRecoveryGuidance old newDb
          finishCommand "migrate" (Error message)
        | _ -> finishCommand "migrate" (Error message)
      | Error _ -> finishCommand "migrate" (Error message)
    | Error _ -> finishCommand "migrate" (Error message)
  | Ok exitCode -> exitCode

let offline (args: ParseResults<OfflineArgs>) =
  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "offline" (args.TryGetResult OfflineArgs.Dir)
      let directoryName = DirectoryInfo(currentDirectory).Name

      let! compiledModule =
        resolveRequiredCompiledModuleForCommand
          "offline"
          currentDirectory
          (args.TryGetResult OfflineArgs.Assembly)
          (args.TryGetResult OfflineArgs.Module)

      let newDb = compiledModule.newDbPath
      let! sourceDb = resolveMigrationSourceDb currentDirectory directoryName newDb

      match sourceDb with
      | None ->
        printfn "Offline migration skipped."
        printCompiledModuleInfo compiledModule
        printfn $"Database already present for current schema: {newDb}"
        return 0
      | Some old ->
        let! migrateResult =
          runOfflineMigrateFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
          |> _.Result
          |> Result.mapError formatExceptionDetails

        let! cleanupResult =
          runArchiveOld currentDirectory old
          |> _.Result
          |> Result.mapError (fun ex -> $"after creating new database: {formatExceptionDetails ex}")

        let previousMarkerStatus =
          cleanupResult.previousMarkerStatus |> Option.defaultValue "no marker"

        let replacedExistingArchive =
          if cleanupResult.replacedExistingArchive then
            "yes"
          else
            "no"

        printfn "Offline migration complete."
        printfn $"Old database: {old}"
        printCompiledModuleInfo compiledModule
        printfn $"New database: {migrateResult.newDbPath}"
        printfn $"Copied tables: {migrateResult.copiedTables}"
        printfn $"Copied rows: {migrateResult.copiedRows}"
        printfn $"Previous old marker status: {previousMarkerStatus}"
        printfn $"Archived database: {cleanupResult.archivePath}"
        printfn $"Replaced existing archive: {replacedExistingArchive}"
        printfn "Hot-migration tables were not created."
        return 0
    }

  finishCommand "offline" result

let init (args: ParseResults<InitArgs>) =
  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "init" (args.TryGetResult InitArgs.Dir)

      let! assemblyPath, moduleName =
        resolveRequiredCompiledMode
          "init"
          currentDirectory
          (args.TryGetResult InitArgs.Assembly)
          (args.TryGetResult InitArgs.Module)

      let! report =
        initDbFromAssemblyModulePath currentDirectory assemblyPath moduleName
        |> fun task -> task.Result

      writeInitDbReport (printfn "%s") report
      return 0
    }

  finishCommand "init" result

let plan (args: ParseResults<PlanArgs>) =
  let printLines header lines =
    printfn $"{header}"

    match lines with
    | [] -> printfn "  - none"
    | values -> values |> List.iter (fun line -> printfn $"  - {line}")

  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "plan" (args.TryGetResult PlanArgs.Dir)
      let directoryName = DirectoryInfo(currentDirectory).Name

      let! compiledModule =
        resolveRequiredCompiledModuleForCommand
          "plan"
          currentDirectory
          (args.TryGetResult PlanArgs.Assembly)
          (args.TryGetResult PlanArgs.Module)

      let newDb = compiledModule.newDbPath
      let! sourceDb = resolveMigrationSourceDb currentDirectory directoryName newDb

      match sourceDb with
      | None ->
        printfn "Plan skipped."
        printCompiledModuleInfo compiledModule
        printfn $"Database already present for current schema: {newDb}"
        return 0
      | Some old ->
        let! report =
          getMigratePlanFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
          |> fun t -> t.Result
          |> Result.mapError formatExceptionDetails

        let canRunMigrate = if report.canRunMigrate then "yes" else "no"

        printfn "Migration plan."
        printfn $"Old database: {old}"
        printCompiledModuleInfo compiledModule
        printfn $"Schema hash: {report.schemaHash}"

        match report.schemaCommit with
        | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
        | None -> ()

        printfn $"New database: {newDb}"
        printfn $"Can run migrate now: {canRunMigrate}"

        printLines "Planned copy targets (execution order):" report.plannedCopyTargets
        printLines "Supported differences:" report.supportedDifferences
        printLines "Unsupported differences:" report.unsupportedDifferences
        printLines "Replay prerequisites:" report.replayPrerequisites

        return if report.canRunMigrate then 0 else 1
    }

  finishCommand "plan" result

let drain (args: ParseResults<DrainArgs>) =
  match resolveCommandDirectory "drain" (args.TryGetResult DrainArgs.Dir) with
  | Error message ->
    eprintfn $"drain failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let setupResult =
      result {
        let! newDb =
          resolveCompiledModeTargetDbPathForCommand
            "drain"
            currentDirectory
            (args.TryGetResult DrainArgs.Assembly)
            (args.TryGetResult DrainArgs.Module)

        let! old = inferOldDbWithExcludedTarget currentDirectory directoryName newDb
        return old, newDb
      }

    match setupResult with
    | Error message ->
      eprintfn $"drain failed: {message}"
      1
    | Ok(old, newDb) ->
      match runDrain old newDb |> fun t -> t.Result with
      | Ok result ->
        printfn "Drain complete."
        printfn $"Old database: {old}"
        printfn $"New database: {newDb}"
        printfn $"Replayed entries: {result.replayedEntries}"
        printfn $"Remaining log entries: {result.remainingEntries}"
        printfn "Run `mig cutover` when ready."
        0
      | Error ex ->
        eprintfn $"drain failed: {formatExceptionDetails ex}"
        1

let cutover (args: ParseResults<CutoverArgs>) =
  let result =
    result {
      let! currentDirectory = resolveCommandDirectory "cutover" (args.TryGetResult CutoverArgs.Dir)
      let directoryName = DirectoryInfo(currentDirectory).Name

      let! newDb =
        resolveCompiledModeTargetDbPathForCommand
          "cutover"
          currentDirectory
          (args.TryGetResult CutoverArgs.Assembly)
          (args.TryGetResult CutoverArgs.Module)

      let oldDb =
        inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)
        |> Result.toOption

      let! cutoverResult =
        match oldDb with
        | Some oldDb -> runCutoverWithOldSafety oldDb newDb |> fun t -> t.Result
        | None -> runCutover newDb |> fun t -> t.Result
        |> Result.mapError formatExceptionDetails

      let droppedIdMapping = if cutoverResult.idMappingDropped then "yes" else "no"

      let droppedMigrationProgress =
        if cutoverResult.migrationProgressDropped then
          "yes"
        else
          "no"

      printfn "Cutover complete."
      printfn $"New database: {newDb}"
      printfn $"Previous migration status: {cutoverResult.previousStatus}"
      printfn "Current migration status: ready"
      printfn $"Dropped _id_mapping: {droppedIdMapping}"
      printfn $"Dropped _migration_progress: {droppedMigrationProgress}"
      return 0
    }

  finishCommand "cutover" result

let archiveOld (args: ParseResults<ArchiveOldArgs>) =
  match resolveCommandDirectory "archive-old" (args.TryGetResult ArchiveOldArgs.Dir) with
  | Error message ->
    eprintfn $"archive-old failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let setupResult =
      result {
        let! inferredNew =
          resolveOptionalCompiledModeTargetDbPathForCommand
            "archive-old"
            currentDirectory
            (args.TryGetResult ArchiveOldArgs.Assembly)
            (args.TryGetResult ArchiveOldArgs.Module)

        let! old =
          inferOldDbFromCurrentDirectory currentDirectory directoryName inferredNew
          |> Result.mapError (fun message ->
            match inferredNew with
            | Some inferredTarget ->
              $"{message} Excluding inferred target '{inferredTarget}'. Use `-d` to select a different directory."
            | None -> $"{message} Use `-d` to select a different directory.")

        return old
      }

    match setupResult with
    | Error message ->
      eprintfn $"archive-old failed: {message}"
      1
    | Ok old ->
      match runArchiveOld currentDirectory old |> fun t -> t.Result with
      | Ok result ->
        let previousMarkerStatus =
          result.previousMarkerStatus |> Option.defaultValue "no marker"

        let replacedExistingArchive = if result.replacedExistingArchive then "yes" else "no"

        printfn "Old database archive complete."
        printfn $"Old database: {old}"
        printfn $"Previous marker status: {previousMarkerStatus}"
        printfn $"Archived database: {result.archivePath}"
        printfn $"Replaced existing archive: {replacedExistingArchive}"
        0
      | Error ex ->
        eprintfn $"archive-old failed: {formatExceptionDetails ex}"
        1

let reset (args: ParseResults<ResetArgs>) =
  let isDryRun = args.Contains ResetArgs.Dry_Run

  match resolveCommandDirectory "reset" (args.TryGetResult ResetArgs.Dir) with
  | Error message ->
    eprintfn $"reset failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let setupResult =
      result {
        let! newDb =
          resolveCompiledModeTargetDbPathForCommand
            "reset"
            currentDirectory
            (args.TryGetResult ResetArgs.Assembly)
            (args.TryGetResult ResetArgs.Module)

        let! old = inferOldDbWithExcludedTarget currentDirectory directoryName newDb
        return old, newDb
      }

    match setupResult with
    | Error message ->
      eprintfn $"reset failed: {message}"
      1
    | Ok(old, newDb) ->
      if isDryRun then
        match getResetMigrationPlan old newDb |> fun t -> t.Result with
        | Error ex ->
          eprintfn $"reset failed: {formatExceptionDetails ex}"
          1
        | Ok plan ->
          let previousOldMarkerStatus =
            plan.previousOldMarkerStatus |> Option.defaultValue "no marker"

          let wouldDropOldMarker = if plan.willDropOldMarker then "yes" else "no"
          let wouldDropOldLog = if plan.willDropOldLog then "yes" else "no"

          let previousNewStatus =
            plan.previousNewStatus |> Option.defaultValue "no status marker"

          let newDatabaseExisted = if plan.newDatabaseExisted then "yes" else "no"
          let wouldDeleteNewDatabase = if plan.willDeleteNewDatabase then "yes" else "no"
          let resetCanApply = if plan.canApplyReset then "yes" else "no"

          printfn "Migration reset dry run."
          printfn $"Old database: {old}"
          printfn $"Previous old marker status: {previousOldMarkerStatus}"
          printfn $"Would drop _migration_marker: {wouldDropOldMarker}"
          printfn $"Would drop _migration_log: {wouldDropOldLog}"
          printfn $"New database: {newDb}"
          printfn $"New database existed: {newDatabaseExisted}"

          if plan.newDatabaseExisted then
            printfn $"Previous new migration status: {previousNewStatus}"

          printfn $"Would delete new database: {wouldDeleteNewDatabase}"
          printfn $"Reset can be applied: {resetCanApply}"

          if not plan.canApplyReset then
            let blockedReason = plan.blockedReason |> Option.defaultValue "Reset is blocked."
            printfn $"Blocked reason: {blockedReason}"

          if plan.canApplyReset then 0 else 1
      else
        match runResetMigration old newDb |> fun t -> t.Result with
        | Error ex ->
          eprintfn $"reset failed: {formatExceptionDetails ex}"
          1
        | Ok result ->
          let previousOldMarkerStatus =
            result.previousOldMarkerStatus |> Option.defaultValue "no marker"

          let droppedOldMarker = if result.oldMarkerDropped then "yes" else "no"
          let droppedOldLog = if result.oldLogDropped then "yes" else "no"

          let previousNewStatus =
            result.previousNewStatus |> Option.defaultValue "no status marker"

          let newDatabaseExisted = if result.newDatabaseExisted then "yes" else "no"
          let newDatabaseDeleted = if result.newDatabaseDeleted then "yes" else "no"

          printfn "Migration reset complete."
          printfn $"Old database: {old}"
          printfn $"Previous old marker status: {previousOldMarkerStatus}"
          printfn $"Dropped _migration_marker: {droppedOldMarker}"
          printfn $"Dropped _migration_log: {droppedOldLog}"
          printfn $"New database: {newDb}"
          printfn $"New database existed: {newDatabaseExisted}"

          if result.newDatabaseExisted then
            printfn $"Previous new migration status: {previousNewStatus}"

          printfn $"Deleted new database: {newDatabaseDeleted}"
          0

let status (args: ParseResults<StatusArgs>) =
  match resolveCommandDirectory "status" (args.TryGetResult StatusArgs.Dir) with
  | Error message ->
    eprintfn $"status failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let setupResult =
      result {
        let! inferredNew =
          result {
            let! candidate =
              resolveOptionalCompiledModeTargetDbPathForCommand
                "status"
                currentDirectory
                (args.TryGetResult StatusArgs.Assembly)
                (args.TryGetResult StatusArgs.Module)

            return candidate |> Option.filter File.Exists
          }

        return inferredNew
      }

    match setupResult with
    | Error message ->
      eprintfn $"status failed: {message}"
      1
    | Ok inferredNew ->
      let inferredOld =
        inferOldDbFromCurrentDirectory currentDirectory directoryName inferredNew

      match inferredOld, inferredNew with
      | Ok oldPath, _ ->
        match getStatus oldPath inferredNew |> fun t -> t.Result with
        | Error ex ->
          eprintfn $"status failed: {formatExceptionDetails ex}"
          1
        | Ok report ->
          let markerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"
          printfn $"Old database: {oldPath}"
          printfn $"Marker status: {markerStatus}"
          printfn $"Migration log entries: {report.migrationLogEntries}"

          match inferredNew with
          | Some newPath ->
            let migrationStatus =
              report.newMigrationStatus |> Option.defaultValue "no status marker"

            let isReady =
              report.newMigrationStatus
              |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

            let pendingReplayText =
              match report.pendingReplayEntries with
              | Some pending when isReady -> $"{pending} (cutover complete)"
              | Some pending -> $"{pending}"
              | None -> "n/a"

            printfn $"New database: {newPath}"
            printfn $"Migration status: {migrationStatus}"

            match report.schemaIdentityHash with
            | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
            | None -> ()

            match report.schemaIdentityCommit with
            | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
            | None -> ()

            printfn $"Pending replay entries: {pendingReplayText}"

            match report.idMappingTablePresent, report.idMappingEntries with
            | Some false, _ -> printfn "_id_mapping: removed"
            | Some true, Some entries -> printfn $"_id_mapping entries: {entries}"
            | _ -> ()

            match report.migrationProgressTablePresent with
            | Some false -> printfn "_migration_progress: removed"
            | Some true -> printfn "_migration_progress: present"
            | None -> ()
          | None -> ()

          0
      | Error _, Some newPath ->
        match getNewDatabaseStatus newPath |> fun t -> t.Result with
        | Error ex ->
          eprintfn $"status failed: {formatExceptionDetails ex}"
          1
        | Ok report ->
          let migrationStatus =
            report.newMigrationStatus |> Option.defaultValue "no status marker"

          printfn "Old database: n/a (not inferred)"
          printfn "Marker status: n/a"
          printfn "Migration log entries: n/a"
          printfn $"New database: {newPath}"
          printfn $"Migration status: {migrationStatus}"

          match report.schemaIdentityHash with
          | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
          | None -> ()

          match report.schemaIdentityCommit with
          | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
          | None -> ()

          printfn "Pending replay entries: n/a (old database unavailable)"

          match report.idMappingTablePresent with
          | false -> printfn "_id_mapping: removed"
          | true -> printfn $"_id_mapping entries: {report.idMappingEntries}"

          match report.migrationProgressTablePresent with
          | false -> printfn "_migration_progress: removed"
          | true -> printfn "_migration_progress: present"

          0
      | Error message, None ->
        eprintfn $"status failed: {message} Use `-d` to select a different directory."
        1

[<EntryPoint>]
let main argv =
  let parser = ArgumentParser.Create<Command>(programName = "mig")

  try
    let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

    if results.Contains Version then
      printfn $"{getVersionText ()}"
      0
    else
      match results.GetSubCommand() with
      | Init args -> init args
      | Codegen args -> codegen args
      | Migrate args -> migrate args
      | Offline args -> offline args
      | Plan args -> plan args
      | Drain args -> drain args
      | Cutover args -> cutover args
      | ArchiveOld args -> archiveOld args
      | Reset args -> reset args
      | Status args -> status args
      | Version ->
        printfn $"{getVersionText ()}"
        0
  with :? ArguParseException as ex ->
    printfn $"%s{ex.Message}"
    1
