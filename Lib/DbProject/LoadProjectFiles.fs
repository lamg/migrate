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

module internal Migrate.DbProject.LoadProjectFiles

open System.IO
open System.Reflection
open Migrate.Types
open Migrate.DbProject.ParseDbToml
open Migrate.DbProject.BuildProject

let loadResourceFile (asm: Assembly) (resourcePrefix: string) (file: string) =
  let filePath = $"{resourcePrefix}.{file}"

  try
    use stream = asm.GetManifestResourceStream filePath
    use file = new StreamReader(stream)
    file.ReadToEnd()
  with ex ->
    FailedLoadResFile $"failed loading resource file {filePath}: {ex.Message}"
    |> raise

let loadProjectWith (loadFile: string -> string) =
  let dbToml = loadFile projectFileName
  let src = parseDbToml dbToml
  buildProject loadFile src

let loadProjectFromDir (dir: string option) =
  let d = Option.defaultValue (Directory.GetCurrentDirectory()) dir

  let loadFile f =
    use f = new StreamReader(Path.Combine(d, f))
    f.ReadToEnd()

  loadProjectWith loadFile
