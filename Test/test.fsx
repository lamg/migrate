#r "../Lib/bin/Debug/net8.0/Migrate.dll"
#r "nuget: Fabulous.AST, 1.0.0-pre9"
#r "nuget: Fantomas.Core, 6.3.3"

open Migrate.FsGeneration.QueryModule
open Migrate.Checks.Types
open Migrate.Types
open Fantomas.Core
open Fabulous.AST
open SyntaxOak
open type Fabulous.AST.Ast

let r =
  { name = "table1"
    columns = [ "col0", SqlInteger ] }

// let fields: WidgetBuilder<FieldNode> list =
//   r.columns
//   |> List.map (fun (name, colType) ->
//     let ``type`` =
//       match colType with
//       | SqlInteger -> "int64"
//       | SqlReal -> "double"
//       | SqlText -> "string"

//     Field(name, ``type``))

// Oak() { AnonymousModule() { Record(r.name) { yield! fields } } }
// |> Gen.mkOak
// |> CodeFormatter.FormatOakAsync
// |> Async.RunSynchronously
// |> printfn "%s"

queryModule [ r ] |> toFsString |> printfn "%s"
