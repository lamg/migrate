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

module internal Migrate.SqlGeneration.View

open Migrate.Types

let sqlCreateView (view: CreateView) =
  let sb = System.Text.StringBuilder()
  let sw = new SqlParser.SqlTextWriter(sb)
  view.selectUnion.ToSql sw
  [ $"CREATE VIEW {view.name} AS\n{sw}" ]

let sqlDropView (view: CreateView) = [ $"DROP VIEW {view.name}" ]
