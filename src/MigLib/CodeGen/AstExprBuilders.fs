/// Helper functions for building Fabulous.AST expressions for query generation.
/// Provides reusable AST builders for common patterns like try/with blocks,
/// method declarations, and SQL command execution.
module internal Mig.CodeGen.AstExprBuilders

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

let returnFromExprRaw (expr: string) = returnFromExpr (rawExpr expr)

let lambdaExpr (parameter: string) (body: WidgetBuilder<Expr>) = ParenLambdaExpr(parameter, body)

let lambdaRawExpr (parameter: string) (body: string) = lambdaExpr parameter (rawExpr body)

let lambdaStatementsExpr (parameter: string) (statements: string seq) =
  lambdaExpr parameter (rawStatementsExpr statements)

/// Build a try/with expression for async code that catches SqliteException and returns Error ex.
/// bodyExprs: sequence of computation expression statements (OtherExpr values)
let trySqliteExceptionAsync (bodyExprs: WidgetBuilder<ComputationExpressionStatement> seq) =
  TryWithExpr(
    CompExprBodyExpr bodyExprs,
    [ MatchClauseExpr(":? SqliteException as ex", ConstantExpr "return Error ex") ]
  )

/// Wrap body in task { } computation expression
let taskExpr (bodyExprs: WidgetBuilder<ComputationExpressionStatement> seq) =
  NamedComputationExpr("task", CompExprBodyExpr bodyExprs)

/// Build a static member on a type augmentation.
/// name: method name e.g. "Delete"
/// parameters: method parameters e.g. `(id: int64)` and `(tx: SqliteTransaction)`
/// body: the method body expression
/// returnType: the return type as string e.g. "Result<unit, SqliteException>"
let staticMember
  (name: string)
  (parameters: WidgetBuilder<Pattern> seq)
  (body: WidgetBuilder<Expr>)
  (returnType: string)
  =
  Member(name, parameters, body, returnType).toStatic ()

/// Fantomas configuration: 2-space indentation, wide lines to match original output
let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2
      MaxLineLength = 200
      SpaceBeforeMember = true }

/// Generate code for a type augmentation.
/// typeName: the type name to augment e.g. "Student"
/// members: members to add to the augmentation
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
