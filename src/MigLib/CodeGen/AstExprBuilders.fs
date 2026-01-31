/// Helper functions for building Fabulous.AST expressions for query generation.
/// Provides reusable AST builders for common patterns like try/with blocks,
/// method declarations, and SQL command execution.
module internal migrate.CodeGen.AstExprBuilders

open Fabulous.AST
open Fantomas.Core
open Fantomas.Core.SyntaxOak
open type Fabulous.AST.Ast

/// Build a try/with expression that catches SqliteException and returns Error ex.
/// bodyExprs: sequence of computation expression statements (OtherExpr values)
let trySqliteException (bodyExprs: WidgetBuilder<Expr> seq) =
  TryWithExpr(CompExprBodyExpr bodyExprs, [ MatchClauseExpr(":? SqliteException as ex", ConstantExpr "Error ex") ])

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
/// name: method name with parameters e.g. "_.Delete (id: int64) (tx: SqliteTransaction)"
/// body: the method body expression
/// returnType: the return type as string e.g. "Result<unit, SqliteException>"
let staticMember (name: string) (body: WidgetBuilder<Expr>) (returnType: string) =
  Member(name, body, returnType).toStatic ()

/// Fantomas configuration: 2-space indentation, wide lines to match original output
let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2
      MaxLineLength = 200
      SpaceBeforeMember = true }

/// Generate code for a type augmentation with a single static member.
/// typeName: the type name to augment e.g. "Student"
/// memberName: method signature e.g. "Delete (id: int64) (tx: SqliteTransaction)"
/// returnType: the return type as string e.g. "Result<unit, SqliteException>"
/// body: the method body expression
let generateStaticMemberCode typeName memberName returnType body =
  let oak =
    Ast.Oak() { AnonymousModule() { Augmentation typeName { staticMember memberName body returnType } } }
    |> Gen.mkOak
    |> Gen.run

  let formatted =
    CodeFormatter.FormatDocumentAsync(false, oak, formatConfig)
    |> Async.RunSynchronously

  // Extract just the member part (skip "type {typeName} with\n")
  let lines = formatted.Code.Split '\n'
  let memberLines = lines |> Array.skip 1
  memberLines |> String.concat "\n"

let pipeIgnore (expr: WidgetBuilder<Expr>) =
  InfixAppExpr(expr, "|>", Constant "ignore")

let returnOk (expr: WidgetBuilder<Expr>) =
  [ pipeIgnore expr; ConstantExpr "Ok()" ]
