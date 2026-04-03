#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"
#r "nuget: MigLib, 5.2.8"

open System
open System.IO
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open MigLib

if not (Context.isFakeContext ()) then
  let executionContext = Context.FakeExecutionContext.Create false "build.fsx" []
  Context.RuntimeContext.Fake executionContext |> Context.setExecutionContext

let rootDir = Path.GetFullPath __SOURCE_DIRECTORY__
let schemaDir = Path.Combine(rootDir, "Schema")
let schemaProjectPath = Path.Combine(schemaDir, "Schema.fsproj")
let exampleProjectPath = Path.Combine(rootDir, "example.fsproj")

let schemaAssemblyPath =
  Path.Combine(schemaDir, "bin", "Debug", "net10.0", "Schema.dll")

let schemaSourcePath = Path.Combine(schemaDir, "Schema.fs")
let generatedDbPath = Path.Combine(schemaDir, "Db.fs")

let cliArgs =
  let commandLineArgs = Environment.GetCommandLineArgs() |> Array.toList

  match commandLineArgs |> List.tryFindIndex ((=) "--") with
  | Some separatorIndex -> commandLineArgs |> List.skip (separatorIndex + 1)
  | None -> commandLineArgs |> List.skip 1

let requestedTarget =
  let rec loop args =
    match args with
    | "--target" :: target :: _ -> target
    | "-t" :: target :: _ -> target
    | arg :: _ when arg.StartsWith "--target=" -> arg.Substring "--target=".Length
    | _ :: rest -> loop rest
    | [] -> "Run"

  loop cliArgs

let private runDotNetCommand command args =
  let result = DotNet.exec id command args

  if not result.OK then
    failwithf "dotnet %s failed with args: %s" command args

Target.create "Restore" (fun _ ->
  runDotNetCommand "restore" $"\"{schemaProjectPath}\""
  runDotNetCommand "restore" $"\"{exampleProjectPath}\"")

Target.create "BuildSchema" (fun _ -> runDotNetCommand "build" $"\"{schemaProjectPath}\" --no-restore")

Target.create "Codegen" (fun _ ->
  if not (File.Exists schemaAssemblyPath) then
    failwithf "Expected compiled schema assembly at %s" schemaAssemblyPath

  match Build.runCodegenFromAssemblyModulePath "Db" schemaSourcePath schemaAssemblyPath "Schema" generatedDbPath with
  | Ok _ -> ()
  | Error e -> failwith $"error generating code: {e}")

Target.create "BuildExample" (fun _ -> runDotNetCommand "build" $"\"{exampleProjectPath}\" --no-restore")

Target.create "Run" (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

"Restore" ==> "BuildSchema" ==> "Codegen" ==> "BuildExample" ==> "Run"

Target.runOrDefault requestedTarget
