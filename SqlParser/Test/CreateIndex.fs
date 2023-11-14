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

module CreateIndex

open Xunit
open SqlParser.Types
open Util

[<Fact>]
let createIndex () =
  let cases =
    [ "INDEX IF NOT EXISTS index0 ON table0(id);",
      { name = "index0"
        table = "table0"
        column = "id" } ]

  cases
  |> List.iteri (fun i -> parseStatementTest $"createIndex-{i}" SqlParser.CreateIndex.createIndex)
