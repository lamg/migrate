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


let loadProjectFiles (basePath: string) (p: DbTomlFile) =
  let reader (file: string) =
    use f = new StreamReader(Path.Combine(basePath, file))
    (file, f.ReadToEnd())

  buildProject reader p

let loadProjectFromRes (asm: Assembly) =
  let loadFile = Migrate.Print.loadFromRes asm
  let baseName = asm.GetName().Name
  let _, dbToml = loadFile baseName projectFileName
  let src = parseDbTomlFile dbToml

  buildProject (loadFile baseName) src

let loadProject () =
  let currDir = System.Environment.CurrentDirectory
  let projFilePath = Path.Combine(currDir, projectFileName)

  let src = parseDbTomlFile projFilePath
  src |> loadProjectFiles currDir
