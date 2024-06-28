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

module internal Migrate.FsGeneration.Main

open System.IO
open Migrate.Types
open Migrate.Checks.Types

let generateDatabaseProj (dir: string option) (p: Project) =
  let dir = Option.defaultValue (Directory.GetCurrentDirectory()) dir
  p |> FsprojFile.projectToFsproj |> FsprojFile.saveXmlTo dir
  let rs, errs = typeCheck p.source

  match errs with
  | [] ->
    let queryFs =
      rs |> relationTypes |> QueryModule.queryModule |> QueryModule.toFsString

    let queryPath = Path.Join(dir, "Query.fs")
    File.WriteAllText(queryPath, queryFs)
    1
  | _ ->
    errs |> String.concat "\n" |> LamgEnv.errPrint
    0
