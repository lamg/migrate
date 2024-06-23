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

module Migrate.FsGeneration.FsGeneration

open Migrate.Types
open Migrate.Checks.Types
open Fantomas.Core
open Fabulous.AST
open SyntaxOak
open type Fabulous.AST.Ast

let relationToFsRecord (r: Relation) =
  let fields: WidgetBuilder<FieldNode> list =
    r.columns
    |> List.map (fun (name, colType) ->
      let ``type`` =
        match colType with
        | SqlInteger -> "int64"
        | SqlReal -> "double"
        | SqlText -> "string"

      Field(name, ``type``))

  Oak() { AnonymousModule() { Record(r.name) { yield! fields } } }

let relationToSelect (r: Relation) =
  let selectBody = ConstantExpr(ConstantUnit())

  Oak() {
    AnonymousModule() { Function($"select{r.name}", [ ParameterPat(ConstantPat(Constant "conn")) ], selectBody) }
  }

let toFsString (xs: WidgetBuilder<Oak>) =
  xs |> Gen.mkOak |> CodeFormatter.FormatOakAsync |> Async.RunSynchronously
