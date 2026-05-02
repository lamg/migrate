namespace Mig

open System
open System.IO
open MigLib
open MigLib.Db
open MigLib.Util

module internal ProgramResolution =
  let resolveCommandDirectory (commandName: string) (candidate: string option) : Result<string, string> =
    let targetDirectory =
      candidate
      |> Option.defaultValue (Directory.GetCurrentDirectory())
      |> Path.GetFullPath

    if Directory.Exists targetDirectory then
      Ok targetDirectory
    else
      Error $"Directory does not exist for `{commandName}`: {targetDirectory}"

  let private discoverRuntimeProjectPath (commandName: string) (currentDirectory: string) : Result<string, string> =
    let projectFiles =
      Directory.GetFiles(currentDirectory, "*.fsproj")
      |> Array.filter (fun path ->
        not (String.Equals(Path.GetFileName path, "MigSchema.fsproj", StringComparison.OrdinalIgnoreCase)))
      |> Array.sort

    match projectFiles with
    | [||] ->
      Error
        $"Could not discover a runtime project for `{commandName}`. No .fsproj file was found in {currentDirectory}."
    | [| projectPath |] -> Ok(Path.GetFullPath projectPath)
    | many ->
      let projectList = many |> Array.map Path.GetFileName |> String.concat ", "

      Error
        $"Could not discover a runtime project for `{commandName}`. Found multiple .fsproj files in {currentDirectory}: {projectList}."

  let createMigProject
    (commandName: string)
    (currentDirectory: string)
    (instance: string option)
    : Result<MigProject, string> =
    result {
      let! fsProject = discoverRuntimeProjectPath commandName currentDirectory

      return
        { fsProject = fsProject
          dbInstance = resolveDatabaseInstance instance
          dbDir = currentDirectory }
    }
