/// Module providing helpers for generating F# code using Fantomas Oak AST.
/// Used for constructs that Fabulous.AST doesn't support well (e.g., type extensions with static methods).
module internal migrate.CodeGen.FabulousAstHelpers

open Fantomas.FCS.Text
open Fantomas.Core
open Fantomas.Core.SyntaxOak

/// Create a SingleTextNode with zero range (for generated code)
let text (s: string) = SingleTextNode(s, Range.Zero)

/// Create an identifier expression
let identExpr (name: string) = Expr.Ident(text name)

/// Create a constant expression from text
let constantExpr (value: string) =
  Expr.Constant(Constant.FromText(text value))

/// Fantomas configuration with 2-space indentation
let private formatConfig =
  { FormatConfig.Default with IndentSize = 2 }

/// Format an Oak AST to F# code string
let formatOak (oak: Oak) : string =
  CodeFormatter.FormatOakAsync(oak, formatConfig)
  |> Async.RunSynchronously

/// Format F# code string using Fantomas
let formatCode (code: string) : string =
  let result =
    CodeFormatter.FormatDocumentAsync(false, code, formatConfig)
    |> Async.RunSynchronously

  result.Code

/// Create a type augmentation (type X with ...) with static members
let createTypeAugmentation (typeName: string) (members: MemberDefn list) : Oak =
  let typeNameNode =
    IdentListNode([ IdentifierOrDot.Ident(text typeName) ], Range.Zero)

  let typeAugmentation =
    TypeDefnAugmentationNode(
      TypeNameNode(
        None, // xmlDoc
        None, // attributes
        text "type", // leadingKeyword
        None, // ao (access modifier)
        typeNameNode, // identifier
        None, // typeParams
        [], // constraints
        None, // implicitConstructor
        None, // equalsToken
        Some(text "with"), // withKeyword
        Range.Zero
      ),
      members,
      Range.Zero
    )

  Oak(
    [],
    [ ModuleOrNamespaceNode(None, [ ModuleDecl.TypeDefn(TypeDefn.Augmentation typeAugmentation) ], Range.Zero) ],
    Range.Zero
  )

/// Create a static member binding for a method
let createStaticMethod
  (methodName: string)
  (parameters: string)
  (returnType: string option)
  (bodyCode: string)
  : MemberDefn =
  let bodyExpr = constantExpr bodyCode

  let memberBinding =
    BindingNode(
      None, // xmlDoc
      None, // attributes
      MultipleTextsNode([ text "static"; text "member" ], Range.Zero), // leadingKeyword
      false, // isMutable
      None, // inlineNode
      None, // accessibility
      Choice1Of2(IdentListNode([ IdentifierOrDot.Ident(text $"{methodName} {parameters}") ], Range.Zero)), // functionName
      None, // genericTypeParameters
      [], // parameters
      returnType
      |> Option.map (fun rt ->
        BindingReturnInfoNode(
          text ":",
          Type.LongIdent(IdentListNode([ IdentifierOrDot.Ident(text rt) ], Range.Zero)),
          Range.Zero
        )), // returnType
      text "=", // equals
      bodyExpr, // expr
      Range.Zero // range
    )

  MemberDefn.Member(memberBinding)
