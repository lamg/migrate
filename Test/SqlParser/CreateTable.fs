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

module CreateTable

open Xunit
open Migrate.SqlParser.Types
open Util


[<Fact>]
let createTable () =
  let cases =
    [ "TABLE table0 (id integer NOT NULL);",
      { name = "table0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [ NotNull ] } ]
        constraints = [] }
      "TABLE table0 (id integer PRIMARY KEY);",
      { name = "table0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [ PrimaryKey None ] } ]
        constraints = [] }
      "TABLE t0 (id integer NOT NULL, UNIQUE(id));",
      { name = "t0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [ NotNull ] } ]
        constraints = [ Unique [ "id" ] ] }
      "TABLE t0 (id integer, name text, PRIMARY KEY(id, name));",
      { name = "t0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [] }
            { name = "name"
              ``type`` = SqlText
              constraints = [] } ]
        constraints = [ PrimaryKeyCols [ "id"; "name" ] ] }
      "TABLE t0(id integer PRIMARY KEY AUTOINCREMENT);",
      { name = "t0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [ PrimaryKey(Some Autoincrement) ] } ]
        constraints = [] }
      "TABLE t0(id integer PRIMARY KEY AUTOINCREMENT, userId text NOT NULL, FOREIGN KEY (userId) REFERENCES user(id));",
      { name = "t0"
        columns =
          [ { name = "id"
              ``type`` = SqlInteger
              constraints = [ PrimaryKey(Some Autoincrement) ] }
            { name = "userId"
              ``type`` = SqlText
              constraints = [ NotNull ] } ]
        constraints =
          [ ForeignKey
              { columns = [ "userId" ]
                refTable = "user"
                refColumns = [ "id" ] } ] } ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"createTable-{i}" Migrate.SqlParser.CreateTable.createTable)
