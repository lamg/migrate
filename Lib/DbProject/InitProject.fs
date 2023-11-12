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

module internal Migrate.DbProject.InitProject

open Migrate.Types
open System.IO

let writeFile file (content: string) =
  if File.Exists(file) then
    MalformedProject $"file {file} already exists" |> raise
  else
    use f = File.CreateText(file)
    f.WriteLine(content)

let initProject () =
  let projectContent =
    """
version_remarks = "project initialization" 
schema_version = "0.0.1"
db_file = "new_db"
files = ["schema.sql"]
pull_script = "pull_script"
"""

  let envFile =
    "
new_db=new_db.sqlite3
pull_script=pull.sh"

  let schemaContent = "CREATE TABLE user(id integer NOT NULL, name text NOT NULL);"

  let pullScript =
    "
#!/usr/bin/env bash
echo 'not implemented'
exit 1
"

  let currDir = System.Environment.CurrentDirectory
  let projFilePath = Path.Combine(currDir, ParseDbToml.projectFileName)

  writeFile projFilePath projectContent
  writeFile "schema.sql" schemaContent
  writeFile "pull.sh" pullScript
  writeFile ".env" envFile
