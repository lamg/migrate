/// Module providing helpers for generating F# code using Fantomas.
module internal MigLib.CodeGen.FabulousAstHelpers

open Fantomas.Core

/// Fantomas configuration with 2-space indentation
let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2 }

/// Format F# code string using Fantomas
let formatCode (code: string) : string =
  let result =
    CodeFormatter.FormatDocumentAsync(false, code, formatConfig)
    |> Async.RunSynchronously

  result.Code
