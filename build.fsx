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

let requestedTarget =
    let rec loop args =
        match args with
        | "--target" :: target :: _ -> target
        | "-t" :: target :: _ -> target
        | arg :: _ when arg.StartsWith "--target=" -> arg.Substring "--target=".Length
        | _ :: rest -> loop rest
        | [] -> "InstallGlobal"

    loop cliArgs

let private runDotNetToolCommand args =
    let result = DotNet.exec id "tool" args

    if not result.OK then
        failwithf "dotnet tool command failed: dotnet tool %s" args

let private runDotNetCommand command args =
    let result = DotNet.exec id command args

    if not result.OK then
        failwithf "dotnet %s failed with args: %s" command args

Target.create "Clean" (fun _ ->
    Shell.cleanDirs [ nupkgDir ]
    Directory.CreateDirectory nupkgDir |> ignore)

Target.create "Restore" (fun _ -> runDotNetCommand "restore" $"\"{srcDir}\"")

Target.create "Build" (fun _ -> runDotNetCommand "build" $"\"{migProjectPath}\" -c Release --no-restore")

Target.create "PackTool" (fun _ ->
    let packArgs =
        $"\"{migProjectPath}\" -c Release -o \"{nupkgDir}\" --no-restore /p:PackAsTool=true /p:ToolCommandName=mig /p:PackageId={packageId}"

    runDotNetCommand "pack" packArgs

    Trace.log $"Packed %s{packageId} to %s{nupkgDir}")

Target.create "InstallGlobal" (fun _ ->
    let updateArgs =
        $"update --global {packageId} --add-source \"{nupkgDir}\" --ignore-failed-sources"

    let updateResult = DotNet.exec id "tool" updateArgs

    if updateResult.OK then
        Trace.log $"Updated global %s{packageId}."
    else
        let installArgs =
            $"install --global {packageId} --add-source \"{nupkgDir}\" --ignore-failed-sources"

        runDotNetToolCommand installArgs
        Trace.log $"Installed global %s{packageId}.")

"Clean" ==> "Restore" ==> "Build" ==> "PackTool" ==> "InstallGlobal"

Target.runOrDefault requestedTarget
