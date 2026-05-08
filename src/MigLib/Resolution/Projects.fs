module internal MigLib.Resolution.Projects

open System
open System.IO
open System.Threading.Tasks

open MigLib.Types
open MigLib.Resolution.Types
open MigLib.TaskResult

let private regularError message = Error(MigError.Regular message)

let private isFsProjectPath (path: string) =
  String.Equals(Path.GetExtension path, ".fsproj", StringComparison.OrdinalIgnoreCase)

let private domainModelingProjectPathFor (runtimeProjectDirectory: string) =
  Path.Combine(runtimeProjectDirectory, "DomainModeling", "DomainModeling.fsproj")

let private resolveDatabaseInstance fallbackDbInstance dbInstance =
  if String.IsNullOrWhiteSpace dbInstance then
    fallbackDbInstance
  else
    dbInstance.Trim()

let private loadSourceSchema sourceDbPath : Task<Result<SqlFile option, MigError>> =
  taskResult {
    use connection = MigLib.Sqlite.openConnection sourceDbPath
    let! (schema: SqlFile) = SchemaIntrospection.loadSchemaFromDatabase connection
    return Some schema
  }

let resolveProjectFromGeneratedSchema
  (dbDir: string)
  (dbInstance: string option)
  (targetSchema: ResolvedGeneratedSchemaModule)
  : Task<Result<ResolvedProject, MigError>> =
  taskResult {
    let! fullDbDir = DatabasePaths.resolveDbDirectory dbDir

    let instance =
      resolveDatabaseInstance targetSchema.defaultDbInstance (dbInstance |> Option.defaultValue "")

    let! dbFile = DatabasePaths.buildSchemaBoundDbFileName targetSchema.dbApp instance targetSchema.schemaHash
    let targetDbPath = Path.Combine(fullDbDir, dbFile)

    let! sourceDbPath = DatabasePaths.resolveSourceDbPath fullDbDir targetSchema.dbApp instance targetSchema.schemaHash

    let! sourceDbSchema =
      match sourceDbPath with
      | Some path -> loadSourceSchema path
      | None -> Task.FromResult(Ok None)

    return
      { sourceDbSchema = sourceDbSchema
        sourceDbPath = sourceDbPath
        archiveDir = Path.Combine(fullDbDir, "archive")
        targetSchema = targetSchema
        targetDbPath = targetDbPath }
  }

let resolveProjectLayout (runtimeProjectPath: string) : Result<ResolvedProjectLayout, MigError> =
  if String.IsNullOrWhiteSpace runtimeProjectPath then
    regularError "Runtime project path is empty."
  else
    let fullProjectPath = Path.GetFullPath runtimeProjectPath

    if not (isFsProjectPath fullProjectPath) then
      regularError $"Runtime project path must be an .fsproj file: {fullProjectPath}"
    elif not (File.Exists fullProjectPath) then
      regularError $"Runtime project file was not found: {fullProjectPath}"
    else
      let runtimeProjectDirectory = Path.GetDirectoryName fullProjectPath
      let domainModelingProjectPath = domainModelingProjectPathFor runtimeProjectDirectory
      let domainModelingDirectory = Path.GetDirectoryName domainModelingProjectPath

      if not (File.Exists domainModelingProjectPath) then
        regularError $"DomainModeling project file was not found: {domainModelingProjectPath}"
      else
        Ok
          { runtimeProjectPath = fullProjectPath
            runtimeProjectDirectory = runtimeProjectDirectory
            runtimeProjectName = Path.GetFileNameWithoutExtension fullProjectPath
            domainModelingProjectPath = domainModelingProjectPath
            domainModelingDirectory = domainModelingDirectory }

let discoverProjectLayout (projectDir: string) : Result<ResolvedProjectLayout, MigError> =
  if String.IsNullOrWhiteSpace projectDir then
    regularError "Project discovery directory is empty."
  else
    let fullDirectory = Path.GetFullPath projectDir

    if not (Directory.Exists fullDirectory) then
      regularError $"Project discovery directory was not found: {fullDirectory}"
    else
      let runtimeProjectCandidates =
        Directory.GetFiles(fullDirectory, "*.fsproj") |> Array.sort

      match runtimeProjectCandidates with
      | [||] -> regularError $"Could not discover a runtime project. No .fsproj file was found in {fullDirectory}."
      | [| projectPath |] -> resolveProjectLayout projectPath
      | many ->
        let projectList = many |> Array.map Path.GetFileName |> String.concat ", "

        regularError
          $"Could not discover a runtime project. Found multiple .fsproj files in {fullDirectory}: {projectList}. Pass the project path explicitly."

let resolveProject
  (runtimeProjectPath: string)
  (dbInstance: string)
  (dbDir: string)
  : Task<Result<ResolvedProject, MigError>> =
  taskResult {
    let! layout = resolveProjectLayout runtimeProjectPath
    let! runtimeAssembly = Assemblies.resolveRuntimeAssembly layout

    let! targetSchema =
      GeneratedSchema.resolveGeneratedSchema runtimeAssembly
      |> Result.map _.generatedModule

    let! (project: ResolvedProject) = resolveProjectFromGeneratedSchema dbDir (Some dbInstance) targetSchema
    return project
  }

let discoverProject
  (projectDir: string)
  (dbInstance: string option)
  (dbDir: string)
  : Task<Result<ResolvedProject, MigError>> =
  taskResult {
    let! layout = discoverProjectLayout projectDir
    let instance = dbInstance |> Option.defaultValue ""
    let! (project: ResolvedProject) = resolveProject layout.runtimeProjectPath instance dbDir
    return project
  }
