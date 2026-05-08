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
let domainModelingDir = Path.Combine(rootDir, "DomainModeling")

let domainModelingProjectPath =
  Path.Combine(domainModelingDir, "DomainModeling.fsproj")

let exampleProjectPath = Path.Combine(rootDir, "example.fsproj")
let migProjectPath = Path.Combine(rootDir, "..", "src", "mig", "mig.fsproj")
let generatedDbPath = Path.Combine(domainModelingDir, "Db.fs")
let exampleDbPrefix = "ExampleApp-main"

[<Literal>]
let clean = "clean"

[<Literal>]
let restore = "restore"

[<Literal>]
let buildDomainModeling = "build-domain-modeling"

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

  Directory.GetFiles(rootDir, $"{exampleDbPrefix}-*.sqlite*")
  |> Seq.iter deleteIfExists

  [ Path.Combine(rootDir, "bin")
    Path.Combine(rootDir, "obj")
    Path.Combine(domainModelingDir, "bin")
    Path.Combine(domainModelingDir, "obj") ]
  |> Shell.cleanDirs)

Target.create restore (fun _ ->
  runDotNetCommand "restore" $"\"{migProjectPath}\""
  runDotNetCommand "restore" $"\"{domainModelingProjectPath}\""
  runDotNetCommand "restore" $"\"{exampleProjectPath}\"")

Target.create buildDomainModeling (fun _ -> runDotNetCommand "build" $"\"{domainModelingProjectPath}\" --no-restore")

Target.create codegen (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- codegen -d \"{rootDir}\"")

Target.create buildExample (fun _ -> runDotNetCommand "build" $"\"{exampleProjectPath}\" --no-restore")

Target.create init (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- init -d \"{rootDir}\"")

Target.create run (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

clean ==> restore ==> buildDomainModeling ==> codegen ==> buildExample ==> run

clean ==> restore ==> buildDomainModeling ==> codegen ==> init

Target.runOrDefault target
