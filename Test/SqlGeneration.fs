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

module SqlGenerationTest

open Xunit
open SqlParser.Types

[<Fact>]
let SqlInsertIntoTest () =
  let i =
    { table = "table0"
      columns = [ "col0"; "col1" ]
      values = [] }

  let xs = Migrate.SqlGeneration.InsertInto.sqlInsertInto i
  Assert.Equal(0, xs.Length)
