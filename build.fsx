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
let packageId = "migtool"

let cliArgs =
    let commandLineArgs = Environment.GetCommandLineArgs() |> Array.toList

    match commandLineArgs |> List.tryFindIndex ((=) "--") with
    | Some separatorIndex -> commandLineArgs |> List.skip (separatorIndex + 1)
    | None -> commandLineArgs |> List.skip 1

[<Literal>]
let installTool = "InstallTool"

let requestedTarget =
    let rec loop args =
        match args with
        | "--target" :: target :: _ -> target
        | "-t" :: target :: _ -> target
        | arg :: _ when arg.StartsWith "--target=" -> arg.Substring "--target=".Length
        | _ :: rest -> loop rest
        | [] -> installTool

    loop cliArgs

let private runDotNetToolCommand args =
    let result = DotNet.exec id "tool" args

    if not result.OK then
        failwithf "dotnet tool command failed: dotnet tool %s" args

let private runDotNetCommand command args =
    let result = DotNet.exec id command args

    if not result.OK then
        failwithf "dotnet %s failed with args: %s" command args

[<Literal>]
let clean = "Clean"

Target.create clean (fun _ ->
    Shell.cleanDirs [ nupkgDir ]
    Directory.CreateDirectory nupkgDir |> ignore)

[<Literal>]
let restore = "Restore"

Target.create restore (fun _ -> runDotNetCommand "restore" $"\"{srcDir}\"")

[<Literal>]
let build = "Build"

Target.create build (fun _ -> runDotNetCommand "build" $"\"{migProjectPath}\" -c Release --no-restore")

[<Literal>]
let packTool = "packTool"

Target.create packTool (fun _ ->
    let packArgs =
        $"\"{migProjectPath}\" -c Release -o \"{nupkgDir}\" --no-restore /p:PackAsTool=true /p:ToolCommandName=mig /p:PackageId={packageId}"

    runDotNetCommand "pack" packArgs

    Trace.log $"Packed %s{packageId} to %s{nupkgDir}")

Target.create installTool (fun _ ->
    let uninstallArgs = $"uninstall --global {packageId}"
    let uninstallResult = DotNet.exec id "tool" uninstallArgs

    if uninstallResult.OK then
        Trace.log $"Removed existing global %s{packageId}."

    let installArgs =
        $"install --global {packageId} --add-source \"{nupkgDir}\" --ignore-failed-sources"

    runDotNetToolCommand installArgs
    Trace.log $"Installed global %s{packageId} from local package output.")

clean ==> restore ==> build ==> packTool ==> installTool

Target.runOrDefault requestedTarget
