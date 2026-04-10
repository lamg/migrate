module MigLib.Build

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.HotMigration
open Mig.CodeGen.CodeGen
open MigLib.CompiledSchema
open MigLib.Db
open MigLib.Util

let deriveSchemaBoundDbFileName
  (dbFileNamePrefix: string)
  (schemaPath: string)
  : Result<string, string> =
  deriveDatabaseFileNameFromSourcePath dbFileNamePrefix schemaPath

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

let generateDbCodeFromTypes
  (moduleName: string)
  (dbFileNamePrefix: string)
  (schemaPath: string)
  (types: Type list)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  generateCodeFromTypesWithDbFile moduleName dbFileNamePrefix schemaPath types outputFilePath

let generateDbCodeFromAssemblyModule
  (generatedModuleName: string)
  (dbFileNamePrefix: string)
  (schemaPath: string)
  (assembly: Assembly)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  generateCodeFromAssemblyModuleWithDbFile generatedModuleName dbFileNamePrefix schemaPath assembly schemaModuleName outputFilePath

let generateDbCodeFromAssemblyModulePath
  (generatedModuleName: string)
  (dbFileNamePrefix: string)
  (schemaPath: string)
  (assemblyPath: string)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error "Compiled assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled assembly was not found: {fullAssemblyPath}"
    else
      try
        let assembly = Assembly.LoadFrom fullAssemblyPath
        generateDbCodeFromAssemblyModule generatedModuleName dbFileNamePrefix schemaPath assembly schemaModuleName outputFilePath
      with ex ->
        Error $"Could not load compiled assembly '{fullAssemblyPath}': {ex.Message}"

type CodegenReport =
  { schemaPath: string
    assemblyPath: string
    schemaModuleName: string
    generatedModuleName: string
    outputPath: string
    stats: CodeGenStats }

let getCodegenReportLines (report: CodegenReport) =
  [ "Code generation complete."
    $"Schema source: {report.schemaPath}"
    $"Compiled assembly: {report.assemblyPath}"
    $"Schema module: {report.schemaModuleName}"
    $"Generated module: {report.generatedModuleName}"
    $"Output file: {report.outputPath}"
    $"Normalized tables (DU): {report.stats.NormalizedTables}"
    $"Regular tables (records): {report.stats.RegularTables}"
    $"Views: {report.stats.Views}"
    "Generated files:" ]
  @ (report.stats.GeneratedFiles |> List.map (fun file -> $"  {file}"))

let writeCodegenReport (writeLine: string -> unit) (report: CodegenReport) =
  getCodegenReportLines report |> List.iter writeLine

let runCodegenFromAssemblyModule
  (generatedModuleName: string)
  (dbFileNamePrefix: string)
  (schemaPath: string)
  (assembly: Assembly)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodegenReport, string> =
  generateDbCodeFromAssemblyModule generatedModuleName dbFileNamePrefix schemaPath assembly schemaModuleName outputFilePath
  |> Result.map (fun stats ->
    { schemaPath = Path.GetFullPath schemaPath
      assemblyPath = assembly.Location
      schemaModuleName = schemaModuleName
      generatedModuleName = generatedModuleName
      outputPath = Path.GetFullPath outputFilePath
      stats = stats })

let runCodegenFromAssemblyModulePath
  (generatedModuleName: string)
  (dbFileNamePrefix: string)
  (schemaPath: string)
  (assemblyPath: string)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodegenReport, string> =
  let fullAssemblyPath = Path.GetFullPath assemblyPath

  generateDbCodeFromAssemblyModulePath generatedModuleName dbFileNamePrefix schemaPath fullAssemblyPath schemaModuleName outputFilePath
  |> Result.map (fun stats ->
    { schemaPath = Path.GetFullPath schemaPath
      assemblyPath = fullAssemblyPath
      schemaModuleName = schemaModuleName
      generatedModuleName = generatedModuleName
      outputPath = Path.GetFullPath outputFilePath
      stats = stats })

type InitDbReport =
  { assemblyPath: string
    moduleName: string
    schemaHash: string option
    newDbPath: string
    seededRows: int64
    skipped: bool }

let private resolveInitDbPath
  (databaseDirectory: string)
  (moduleName: string)
  (generatedModule: GeneratedSchemaModule)
  : Result<string, string> =
  match generatedModule.dbFile with
  | Some dbFileName ->
    resolveDatabaseFilePath databaseDirectory dbFileName
    |> Result.mapError (fun message ->
      $"Could not resolve DbFile '{dbFileName}' for compiled module '{moduleName}': {message}")
  | None -> Error $"Compiled module '{moduleName}' does not define DbFile."

let getInitDbReportLines (report: InitDbReport) =
  let header = if report.skipped then "Init skipped." else "Init complete."

  [ header
    $"Compiled assembly: {report.assemblyPath}"
    $"Compiled module: {report.moduleName}" ]
  @ (match report.schemaHash with
     | Some schemaHash -> [ $"Schema hash: {schemaHash}" ]
     | None -> [])
  @ [ if report.skipped then
        $"Database already present for current schema: {report.newDbPath}"
      else
        $"Database: {report.newDbPath}"
      if report.skipped then
        $"Seeded rows: {report.seededRows} (skipped)"
      else
        $"Seeded rows: {report.seededRows}" ]

let writeInitDbReport (writeLine: string -> unit) (report: InitDbReport) =
  getInitDbReportLines report |> List.iter writeLine

let private buildInitDbReport
  (assemblyPath: string)
  (moduleName: string)
  (generatedModule: GeneratedSchemaModule)
  (newDbPath: string)
  (seededRows: int64)
  (skipped: bool)
  =
  { assemblyPath = Path.GetFullPath assemblyPath
    moduleName = moduleName
    schemaHash = generatedModule.schemaHash
    newDbPath = Path.GetFullPath newDbPath
    seededRows = seededRows
    skipped = skipped }

let private mapTaskResultError (mapError: 'error -> 'mappedError) (operation: Task<Result<'a, 'error>>) =
  task {
    let! result = operation
    return result |> Result.mapError mapError
  }

let private runInitWithGeneratedModule
  (assemblyPath: string)
  (databaseDirectory: string)
  (moduleName: string)
  (generatedModule: GeneratedSchemaModule)
  (initOperation: string -> Task<Result<InitResult, SqliteException>>)
  : Task<Result<InitDbReport, string>> =
  taskResult {
    let! newDbPath = resolveInitDbPath databaseDirectory moduleName generatedModule

    if File.Exists newDbPath then
      return buildInitDbReport assemblyPath moduleName generatedModule newDbPath 0L true
    else
      let! (initResult: InitResult) = initOperation newDbPath |> mapTaskResultError formatExceptionDetails

      return buildInitDbReport assemblyPath moduleName generatedModule initResult.newDbPath initResult.seededRows false
  }

let initDbFromAssemblyModule
  (databaseDirectory: string)
  (assembly: Assembly)
  (moduleName: string)
  : Task<Result<InitDbReport, string>> =
  taskResult {
    let! (generatedModule: GeneratedSchemaModule) = tryLoadGeneratedSchemaModuleFromAssembly assembly moduleName

    let! (report: InitDbReport) =
      runInitWithGeneratedModule
        assembly.Location
        databaseDirectory
        moduleName
        generatedModule
        (runInitWithSchema generatedModule.schema)

    return report
  }

let initDbFromAssemblyModulePath
  (databaseDirectory: string)
  (assemblyPath: string)
  (moduleName: string)
  : Task<Result<InitDbReport, string>> =
  taskResult {
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    let! (generatedModule: GeneratedSchemaModule) =
      tryLoadGeneratedSchemaModuleFromAssemblyPath fullAssemblyPath moduleName

    let! (report: InitDbReport) =
      runInitWithGeneratedModule
        fullAssemblyPath
        databaseDirectory
        moduleName
        generatedModule
        (runInitFromAssemblyPath fullAssemblyPath moduleName)

    return report
  }
