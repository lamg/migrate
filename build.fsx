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
let toolInstallDir = Path.Combine(rootDir, ".tools", "mig")
let packageId = "migtool"

let localToolVersion =
    Environment.GetEnvironmentVariable "MIG_LOCAL_VERSION"
    |> Option.ofObj
    |> Option.defaultValue "0.0.0-local"

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
        | [] -> "InstallLocal"

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
    Shell.cleanDirs [ nupkgDir; toolInstallDir ]
    Directory.CreateDirectory nupkgDir |> ignore
    Directory.CreateDirectory toolInstallDir |> ignore)

Target.create "Restore" (fun _ -> runDotNetCommand "restore" $"\"{srcDir}\"")

Target.create "Build" (fun _ -> runDotNetCommand "build" $"\"{migProjectPath}\" -c Release --no-restore")

Target.create "PackTool" (fun _ ->
    let packArgs =
        $"\"{migProjectPath}\" -c Release -o \"{nupkgDir}\" --no-restore /p:PackAsTool=true /p:ToolCommandName=mig /p:PackageId={packageId} /p:Version={localToolVersion}"

    runDotNetCommand "pack" packArgs

    Trace.log $"Packed %s{packageId} %s{localToolVersion} to %s{nupkgDir}")

Target.create "InstallLocal" (fun _ ->
    let uninstallArgs = $"uninstall --tool-path \"{toolInstallDir}\" {packageId}"
    let uninstallResult = DotNet.exec id "tool" uninstallArgs

    if uninstallResult.OK then
        Trace.log $"Uninstalled previous local %s{packageId} from %s{toolInstallDir}."
    else
        Trace.log $"No previous local %s{packageId} installation found in %s{toolInstallDir}."

    let installArgs =
        $"install --tool-path \"{toolInstallDir}\" {packageId} --version {localToolVersion} --add-source \"{nupkgDir}\" --ignore-failed-sources"

    runDotNetToolCommand installArgs
    Trace.log $"Installed tool command `mig` to %s{toolInstallDir}.")

"Clean" ==> "Restore" ==> "Build" ==> "PackTool" ==> "InstallLocal"

Target.runOrDefault requestedTarget
