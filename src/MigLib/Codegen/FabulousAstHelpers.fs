module internal MigLib.Codegen.FabulousAstHelpers

open Fantomas.Core

let private formatConfig =
  { FormatConfig.Default with
      IndentSize = 2 }

let formatCode (code: string) : string =
  let result =
    CodeFormatter.FormatDocumentAsync(false, code, formatConfig)
    |> Async.RunSynchronously

  result.Code
