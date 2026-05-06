#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"

open System
open System.IO
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO

if not (Context.isFakeContext ()) then
  let executionContext = Context.FakeExecutionContext.Create false "build.fsx" []
  Context.RuntimeContext.Fake executionContext |> Context.setExecutionContext

let rootDir = Path.GetFullPath __SOURCE_DIRECTORY__
let schemaDir = Path.Combine(rootDir, "MigSchema")
let schemaProjectPath = Path.Combine(schemaDir, "MigSchema.fsproj")
let exampleProjectPath = Path.Combine(rootDir, "example.fsproj")
let migProjectPath = Path.Combine(rootDir, "..", "src", "mig", "mig.fsproj")
let generatedDbPath = Path.Combine(schemaDir, "Db.fs")
let exampleDbPrefix = "ExampleApp-main"

[<Literal>]
let clean = "clean"

[<Literal>]
let restore = "restore"

[<Literal>]
let buildSchema = "build-schema"

[<Literal>]
let codegen = "codegen"

[<Literal>]
let buildExample = "build-example"

[<Literal>]
let init = "init"

[<Literal>]
let run = "run"

let target =
  match Environment.GetCommandLineArgs() with
  | [| _; _; t |] -> t
  | _ -> run

let private runDotNetCommand command args =
  let result = DotNet.exec id command args

  if not result.OK then
    failwithf "dotnet %s failed with args: %s" command args

let private deleteIfExists path =
  if File.Exists path then
    File.Delete path
  else if Directory.Exists path then
    Directory.delete path

Target.create clean (fun _ ->
  deleteIfExists generatedDbPath

  Directory.GetFiles(rootDir, $"{exampleDbPrefix}-*.sqlite")
  |> Seq.iter deleteIfExists

  [ Path.Combine(rootDir, "bin")
    Path.Combine(rootDir, "obj")
    Path.Combine(schemaDir, "bin")
    Path.Combine(schemaDir, "obj") ]
  |> Shell.cleanDirs)

Target.create restore (fun _ ->
  runDotNetCommand "restore" $"\"{migProjectPath}\""
  runDotNetCommand "restore" $"\"{schemaProjectPath}\""
  runDotNetCommand "restore" $"\"{exampleProjectPath}\"")

Target.create buildSchema (fun _ -> runDotNetCommand "build" $"\"{schemaProjectPath}\" --no-restore")

Target.create codegen (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- codegen -d \"{rootDir}\"")

Target.create buildExample (fun _ -> runDotNetCommand "build" $"\"{exampleProjectPath}\" --no-restore")

Target.create init (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- init -d \"{rootDir}\"")

Target.create run (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

clean ==> restore ==> buildSchema ==> codegen ==> buildExample ==> init ==> run

Target.runOrDefault target
