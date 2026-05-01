module internal MigLib.Commands.Resolution.Projects

open System
open System.IO

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

let private regularError message = Error(MigError.Regular message)

let private isFsProjectPath (path: string) =
  String.Equals(Path.GetExtension path, ".fsproj", StringComparison.OrdinalIgnoreCase)

let private schemaProjectPathFor (runtimeProjectDirectory: string) =
  Path.Combine(runtimeProjectDirectory, "MigSchema", "MigSchema.fsproj")

let resolveProject (project: MigProject) : Result<ResolvedProject, MigError> =
  if String.IsNullOrWhiteSpace project.fsProject then
    regularError "Runtime project path is empty."
  else
    let fullProjectPath = Path.GetFullPath project.fsProject

    if not (isFsProjectPath fullProjectPath) then
      regularError $"Runtime project path must be an .fsproj file: {fullProjectPath}"
    elif not (File.Exists fullProjectPath) then
      regularError $"Runtime project file was not found: {fullProjectPath}"
    else
      let runtimeProjectDirectory = Path.GetDirectoryName fullProjectPath
      let schemaProjectPath = schemaProjectPathFor runtimeProjectDirectory
      let schemaDirectory = Path.GetDirectoryName schemaProjectPath

      if not (File.Exists schemaProjectPath) then
        regularError $"Schema project file was not found: {schemaProjectPath}"
      else
        Ok
          { migProject =
              { project with
                  fsProject = fullProjectPath }
            runtimeProjectPath = fullProjectPath
            runtimeProjectDirectory = runtimeProjectDirectory
            runtimeProjectName = Path.GetFileNameWithoutExtension fullProjectPath
            schemaProjectPath = schemaProjectPath
            schemaDirectory = schemaDirectory }

let discoverProject (directory: string) (dbInstance: string) (dbDir: string) : Result<ResolvedProject, MigError> =
  if String.IsNullOrWhiteSpace directory then
    regularError "Project discovery directory is empty."
  else
    let fullDirectory = Path.GetFullPath directory

    if not (Directory.Exists fullDirectory) then
      regularError $"Project discovery directory was not found: {fullDirectory}"
    else
      let runtimeProjectCandidates =
        Directory.GetFiles(fullDirectory, "*.fsproj") |> Array.sort

      match runtimeProjectCandidates with
      | [||] -> regularError $"Could not discover a runtime project. No .fsproj file was found in {fullDirectory}."
      | [| projectPath |] ->
        resolveProject
          { fsProject = projectPath
            dbInstance = dbInstance
            dbDir = dbDir }
      | many ->
        let projectList = many |> Array.map Path.GetFileName |> String.concat ", "

        regularError
          $"Could not discover a runtime project. Found multiple .fsproj files in {fullDirectory}: {projectList}. Pass the project path explicitly."
