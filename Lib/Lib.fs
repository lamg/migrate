module Migrate.Lib

/// <summary>
/// Convenience function for running commits when using Migrate as a library
/// </summary>
let commitQuiet (p: Types.Project) =
  Migrate.Execution.Commit.migrateAndCommit p true

/// <summary>
/// Convenience function for loading project files from assembly resources
/// Raises MalformedProject in case of failure
/// </summary>
let loadResourceFile (asm: System.Reflection.Assembly) (prefix: string) (file: string) =
  Migrate.DbProject.LoadProjectFiles.loadResourceFile asm prefix file

/// <summary>
/// Loads a project using a custom file reader
/// </summary>
let loadProjectWith (loadFile: string -> string) =
  Migrate.DbProject.LoadProjectFiles.loadProjectWith loadFile

/// <summary>
/// Loads a project from a directory if specified or the current one instead
/// </summary>
let loadProjectFromDir (dir: string option) =
  try
    DbProject.LoadProjectFiles.loadProjectFromDir dir |> Ok
  with e ->
    Error e.Message
