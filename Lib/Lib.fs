// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module Migrate.Lib

/// <summary>
/// Convenience function for running commits when using Migrate as a library
/// </summary>
let commitQuiet (p: Types.Project) =
  Execution.Commit.migrateAndCommit p true

/// <summary>
/// Convenience function for loading project files from assembly resources
/// Raises MalformedProject in case of failure
/// </summary>
let loadResourceFile (asm: System.Reflection.Assembly) (prefix: string) (file: string) =
  DbProject.LoadProjectFiles.loadResourceFile asm prefix file

/// <summary>
/// Loads a project using a custom file reader
/// </summary>
let loadProjectWith (loadFile: string -> string) =
  DbProject.LoadProjectFiles.loadProjectWith loadFile

/// <summary>
/// Loads a project from a directory if specified or the current one instead
/// </summary>
let loadProjectFromDir (dir: string option) =
  try
    DbProject.LoadProjectFiles.loadProjectFromDir dir |> Ok
  with e ->
    Error e.Message

let generateDatabaseProj (dir: string option) (p: Types.Project) =
  FsGeneration.Main.generateDatabaseProj dir p
