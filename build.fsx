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
let srcDir = Path.Combine(rootDir, "src")
let migProjectPath = Path.Combine(srcDir, "mig", "mig.fsproj")
let artifactsDir = Path.Combine(rootDir, "artifacts")
let nupkgDir = Path.Combine(artifactsDir, "nupkg")

[<Literal>]
let packageId = "migtool"

[<Literal>]
let build = "build"

[<Literal>]
let error = "error"

[<Literal>]
let install = "install"

let target =
    match Environment.GetCommandLineArgs() with
    | [| _; _; t |] -> t
    | args -> error

let private runDotNetToolCommand args =
    let result = DotNet.exec id "tool" args

    if not result.OK then
        failwithf "dotnet tool command failed: dotnet tool %s" args

let private runDotNetCommand command args =
    let result = DotNet.exec id command args

    if not result.OK then
        failwithf "dotnet %s failed with args: %s" command args

Target.create build (fun _ -> runDotNetCommand "build" $"\"{migProjectPath}\" -c Release --no-restore")

Target.create install (fun _ ->
    // clean
    Shell.cleanDirs [ nupkgDir ]
    Directory.CreateDirectory nupkgDir |> ignore

    // uninstall
    let uninstallArgs = $"uninstall --global {packageId}"
    let uninstallResult = DotNet.exec id "tool" uninstallArgs

    if uninstallResult.OK then
        Trace.log $"Removed existing global %s{packageId}."

    // pack
    let packArgs =
        $"\"{migProjectPath}\" -c Release -o \"{nupkgDir}\" --no-restore /p:PackAsTool=true /p:ToolCommandName=mig /p:PackageId={packageId}"

    runDotNetCommand "pack" packArgs

    let installArgs =
        $"install --global {packageId} --add-source \"{nupkgDir}\" --ignore-failed-sources"

    runDotNetToolCommand installArgs
    Trace.log $"Installed global %s{packageId} from local package output.")

build ==> install

Target.runOrDefault target
