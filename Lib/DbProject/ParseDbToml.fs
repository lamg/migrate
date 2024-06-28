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

module internal Migrate.DbProject.ParseDbToml

open System
open Migrate.Types
open System.IO

[<Literal>]
let dbFileKey = "db_file"

[<Literal>]
let projectFileName = "db.toml"

[<Literal>]
let schemaVersion = "schema_version"

[<Literal>]
let versionRemarks = "version_remarks"

[<Literal>]
let projectFiles = "files"

[<Literal>]
let pullScript = "pull_script"

[<Literal>]
let tableSync = "table_sync"

[<Literal>]
let tableInit = "table_init"

[<Literal>]
let reportTable = "report"

[<Literal>]
let includeFsFiles = "include_fs_files"

let tryGet (t: Tomlyn.Model.TomlTable) (key: string) =
  if t.ContainsKey(key) then Some(t[key]) else None

let tryGetArray (t: Tomlyn.Model.TomlTable) (key: string) =
  try
    tryGet t key
    |> Option.map (fun s -> s :?> Tomlyn.Model.TomlArray)
    |> function
      | Some xs -> xs |> Seq.map (fun s -> s :?> string) |> Seq.toList
      | _ -> []
  with :? InvalidCastException as e ->
    MalformedProject $"parsing db.toml: key {key} found but {e.Message}" |> raise

let tryGetTableArray (t: Tomlyn.Model.TomlTable) (key: string) =
  try
    tryGet t key |> Option.map (fun s -> s :?> Tomlyn.Model.TomlTableArray)
  with :? InvalidCastException as e ->
    MalformedProject $"parsing db.toml: key {key} found but {e.Message}" |> raise

let tryGetString (t: Tomlyn.Model.TomlTable) (key: string) =
  try
    tryGet t key |> Option.map (fun s -> s :?> string)
  with :? InvalidCastException as e ->
    MalformedProject $"parsing db.toml: key {key} found but {e.Message}" |> raise

let tryGetBool (t: Tomlyn.Model.TomlTable) (key: string) =
  try
    tryGet t key |> Option.map (fun s -> s :?> bool)
  with :? InvalidCastException as e ->
    MalformedProject $"parsing db.toml: key {key} found but {e.Message}" |> raise

/// <summary>
/// Parse a project file.
/// Raises `MalformedProject` in case an environment variable is not defined.
/// </summary>
/// <param name="source"></param>
let parseDbToml (source: string) =
  let doc = Tomlyn.Toml.ToModel source

  let rec getEnvField (ctx: string) (var: string) =
    match Migrate.Print.getEnv var with
    | Some n -> n
    | None -> MalformedProject $"{ctx}: environment variable '{var}' not defined" |> raise

  let syncs = tryGetArray doc tableSync

  let inits = tryGetArray doc tableInit

  let reports =
    match tryGetTableArray doc reportTable with
    | Some xs ->
      xs
      |> Seq.map (fun r ->
        { src = r["src"] :?> string
          dest = r["dest"] :?> string })
      |> Seq.toList
    | _ -> []

  let files = tryGetArray doc projectFiles

  let script =
    tryGetString doc pullScript |> Option.map (getEnvField "db.toml pull_script")

  let version =
    match tryGetString doc schemaVersion with
    | Some v -> v
    | _ -> MalformedProject $"no {schemaVersion} field defined in db.toml" |> raise

  let remarks =
    match tryGetString doc versionRemarks with
    | Some v -> v
    | _ -> MalformedProject $"no {versionRemarks} field defined in db.toml" |> raise

  let included = tryGetArray doc includeFsFiles

  match tryGetString doc dbFileKey with
  | None -> MalformedProject $"no {dbFileKey} defined" |> raise
  | Some f ->
    let dbFile = getEnvField "db.toml db_file" f

    { dbFile = dbFile
      reports = reports
      files = files
      syncs = syncs
      inits = inits
      pullScript = script
      schemaVersion = version
      versionRemarks = remarks
      includeFsFiles = included }

let parseDbTomlFile (path: string) =
  try
    use file = new StreamReader(path)
    file.ReadToEnd() |> parseDbToml
  with :? FileNotFoundException as e ->
    MalformedProject $"{e.Message}" |> raise
