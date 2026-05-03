module internal MigLib.Codegen.AstExprBuilders

open Fabulous.AST
open Fantomas.Core
open Fantomas.Core.SyntaxOak
open type Fabulous.AST.Ast

let unitExpr = ConstantExpr(ConstantUnit())

let rawExpr (value: string) = ConstantExpr(value)

let rawStatementsExpr (statements: string seq) =
  statements |> Seq.map OtherExpr |> CompExprBodyExpr

let typedParenParam (name: string) (paramType: string) = ParenPat(ParameterPat(name, paramType))

let typedTupledOrSingleParam (parameters: (string * string) list) =
  match parameters with
  | [ name, paramType ] -> typedParenParam name paramType
  | _ ->
    parameters
    |> List.map (fun (name, paramType) -> ParameterPat(name, paramType))
    |> TuplePat
    |> ParenPat

let txParam = typedParenParam "tx" "SqliteTransaction"

let returnExpr (expr: WidgetBuilder<Expr>) = SingleExpr("return", expr)

let returnExprRaw (expr: string) = rawExpr $"return {expr}"

let returnFromExpr (expr: WidgetBuilder<Expr>) = SingleExpr("return!", expr)

let lambdaExpr (parameter: string) (body: WidgetBuilder<Expr>) = ParenLambdaExpr(parameter, body)

let lambdaRawExpr (parameter: string) (body: string) = lambdaExpr parameter (rawExpr body)

let lambdaStatementsExpr (parameter: string) (statements: string seq) =
  lambdaExpr parameter (rawStatementsExpr statements)

let taskExpr (bodyExprs: WidgetBuilder<ComputationExpressionStatement> seq) =
  NamedComputationExpr("task", CompExprBodyExpr bodyExprs)

let staticMember
  (name: string)
  (parameters: WidgetBuilder<Pattern> seq)
  (body: WidgetBuilder<Expr>)
  (returnType: string)
  =
  Member(name, parameters, body, returnType).toStatic ()

let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2
      MaxLineLength = 200
      SpaceBeforeMember = true }

let generateAugmentationCode typeName (members: WidgetBuilder<MemberDefn> seq) =
  let oak =
    Ast.Oak() {
      AnonymousModule() {
        Augmentation typeName {
          for memberDef in members do
            memberDef
        }
      }
    }
    |> Gen.mkOak
    |> Gen.run

  let formatted =
    CodeFormatter.FormatDocumentAsync(false, oak, formatConfig)
    |> Async.RunSynchronously

  formatted.Code
