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

module Migrate.SqlParser.Parser

open FParsec
open Migrate.SqlParser.Types

let private toArr = ErrorMessageList.ToSortedArray

let rec private flattenErr (line: int) (col: int) (e: ErrorMessage) =
  let flatten messages l c =
    messages |> toArr |> Seq.map (flattenErr l c) |> Seq.concat |> Seq.toList

  match e with
  | :? ErrorMessage.NestedError as e -> flatten e.Messages (int e.Position.Line) (int e.Position.Column)
  | :? ErrorMessage.ExpectedString as e -> [ Some(line, col, $"expected {e.String}") ]
  | :? ErrorMessage.CompoundError as e ->
    flatten e.NestedErrorMessages (int e.NestedErrorPosition.Line) (int e.NestedErrorPosition.Column)
    |> Seq.toList
  | :? ErrorMessage.Expected as e -> [ Some(line, col, e.Label) ]
  | _ -> [ None ]


let private pointTo (sql: string) (line: int) (col: int) =
  let srcLine =
    sql.Split "\n" |> Array.tryItem (int (line - 1)) |> Option.defaultValue ""

  let pointer = String.replicate (col + line.ToString().Length) " " + "^"
  $"{line}|{srcLine}\n{pointer}"

let parseSql (file: string) (sql: string) : Result<SqlFile, string> =
  match sql, runParserOnString Statements.statements () file sql with
  | _, Success(v, _, p) ->
    // printfn $"ended in {p}"
    Result.Ok v
  | _, Failure(e, s, _) ->
    $"{e}:\n{pointTo sql (int s.Position.Line) (int s.Position.Column)}"
    |> Result.Error
