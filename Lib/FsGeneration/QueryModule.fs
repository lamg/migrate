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

module Migrate.FsGeneration.QueryModule

open System
open Fantomas.Core.SyntaxOak
open Migrate.Types
open Migrate.Checks.Types
open Fantomas.Core
open Fabulous.AST
open SyntaxOak
open type Fabulous.AST.Ast

let firstCharTrans (s: string) (f: Char -> Char) =
  if s = String.empty then
    s
  else
    let c = f s[0]
    let r = s.Substring 1
    r.Insert(0, c.ToString())

let uppercase s = firstCharTrans s Char.ToUpper

let lowercase s = firstCharTrans s Char.ToLower

let pascalCase (s: string) =
  s.Split '_' |> Seq.map uppercase |> String.concat ""

let camelCase = pascalCase >> lowercase

let relationToFsRecord (r: Relation) =
  let fields: WidgetBuilder<FieldNode> list =
    r.columns
    |> List.map (fun (name, colType) ->
      let ``type`` =
        match colType with
        | SqlInteger -> "int64"
        | SqlReal -> "double"
        | SqlText -> "string"

      Field(camelCase name, ``type``))

  Record(pascalCase r.name) { yield! fields }

let relationToSelectAll (r: Relation) =
  let cols = r.columns |> Seq.map fst |> String.concat ", "
  let selectQuery = $"SELECT {cols} FROM {r.name}"

  let readRowBody =
    r.columns
    |> List.mapi (fun i (c, t) ->
      match t with
      | SqlInteger -> RecordFieldExpr(camelCase c, $"rd.GetInt32 {i}")
      | SqlText -> RecordFieldExpr(camelCase c, $"rd.GetString {i}")
      | SqlReal -> RecordFieldExpr(camelCase c, $"rd.GetDouble {i}"))
    |> RecordExpr

  let whileBody =
    CompExprBodyExpr
      [ LetOrUseExpr(Value("v", readRowBody))
        OtherExpr(AppExpr(ConstantExpr(Constant "yield"), ConstantExpr(Constant "v"))) ]

  let selectBody =
    CompExprBodyExpr
      [ LetOrUseExpr(Use("rd", $"env.ExecuteReader(\"{selectQuery}\")"))
        OtherExpr(NamedComputationExpr("seq", WhileExpr(ConstantExpr("rd.Read()"), whileBody))) ]


  Function(
    $"selectAll{pascalCase r.name}",
    [ ParenPat(ParameterPat(NamedPat("env"), LongIdent "ReaderExecuter")) ],
    selectBody
  )

let relationToInsertValues (r: Relation) =
  let cols = r.columns |> Seq.map fst |> String.concat ", "
  let parameters = r.columns |> List.map (fun (c, _) -> $"@{c}") |> String.concat ", "
  let insertQuery = $"INSERT INTO {r.name} ({cols}) VALUES ({parameters})"
  let colsArr = r.columns |> Seq.map (fun (c, _) -> $"\"{c}\"") |> String.concat "; "

  vs
  |> List.map (fun v -> cols |> List.map (fun c -> $"v.{col}") |> List.toArray)
  |> List.toArray
  
  ExprParenLambdaNode()

  let x = ExprParenLambdaNode () ([ Constant "a" ], Int 1)

  let innerListMap = AppExpr(ConstantExpr "List.map", a)

  let lambdaMap =
    InfixAppExpr(ConstantExpr "cols", "|>", InfixAppExpr(innerListMap, "|>", listToArray))

  let insertBody =
    CompExprBodyExpr
      [ LetOrUseExpr(Value("vss", InfixAppExpr(ConstantExpr "vs", "|>", InfixAppExpr(listMap, "|>", toArray)))) // TODO
        OtherExpr(ConstantExpr $"env.Insert \"{insertQuery}\" [|{colsArr}|] vss") ]

  Function(
    $"insert{pascalCase r.name}",
    [ ParenPat(ParameterPat(NamedPat("env"), LongIdent "InsertExecuter"))
      ParenPat(ParameterPat(NamedPat("vs"), $"{pascalCase r.name} list")) ],
    insertBody
  )


let queryModule (rs: Relation list) =

  Oak() {
    TopLevelModule "Database.Query" {
      yield Open "System"
      yield Open "Migrate.FsGeneration.Util"

      for x in rs |> List.map relationToFsRecord do
        yield x

      for x in rs |> List.map relationToSelectAll do
        yield x

      for x in rs |> List.map relationToInsertValues do
        yield x
    }
  }

let toFsString (xs: WidgetBuilder<Oak>) =
  xs |> Gen.mkOak |> CodeFormatter.FormatOakAsync |> Async.RunSynchronously
