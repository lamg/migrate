#r "nuget: Fake.Core.Target, 6.1.3"
#r "nuget: Fake.DotNet.Cli, 6.1.3"
#r "nuget: Fake.IO.FileSystem, 6.1.3"
#r "nuget: Microsoft.Data.Sqlite, 9.0.0"

open System
open System.IO
open Microsoft.Data.Sqlite
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

let generatedDbPath = Path.Combine(rootDir, "Db.fs")
let exampleDbPrefix = "ExampleApp-main"

let legacyDbPath =
  Path.Combine(rootDir, $"{exampleDbPrefix}-1111111111111111.sqlite")

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

let private deleteIfExists path =
  if File.Exists path then
    File.Delete path
  else if Directory.Exists path then
    Directory.delete path

let private schemaHash () =
  if not (File.Exists generatedDbPath) then
    failwithf "Expected generated Db.fs at %s" generatedDbPath

  let content = File.ReadAllText generatedDbPath
  let marker = "let SchemaHash = \""
  let startIndex = content.IndexOf(marker, StringComparison.Ordinal)

  if startIndex < 0 then
    failwith "Could not find SchemaHash in generated Db.fs"

  let hashStart = startIndex + marker.Length
  let hashEnd = content.IndexOf('"', hashStart)

  if hashEnd < 0 then
    failwith "Could not parse SchemaHash in generated Db.fs"

  content.Substring(hashStart, hashEnd - hashStart)

let private targetDbPath () =
  Path.Combine(rootDir, $"{exampleDbPrefix}-{schemaHash ()}.sqlite")

let private createLegacyDatabase () =
  deleteIfExists legacyDbPath
  deleteIfExists "archive"

  use connection = new SqliteConnection($"Data Source={legacyDbPath}")
  connection.Open()

  use createTable =
    new SqliteCommand(
      "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, age INTEGER NOT NULL DEFAULT 18);",
      connection
    )

  createTable.ExecuteNonQuery() |> ignore

  use seedRows =
    new SqliteCommand("INSERT INTO student(name, age) VALUES ('Alice', 21), ('Bob', 24);", connection)

  seedRows.ExecuteNonQuery() |> ignore

Target.create "Clean" (fun _ ->
  deleteIfExists generatedDbPath
  deleteIfExists legacyDbPath

  Directory.GetFiles(rootDir, $"{exampleDbPrefix}-*.sqlite")
  |> Seq.iter deleteIfExists

  [ Path.Combine(rootDir, "bin")
    Path.Combine(rootDir, "obj")
    Path.Combine(schemaDir, "bin")
    Path.Combine(schemaDir, "obj") ]
  |> Shell.cleanDirs)

Target.create "Restore" (fun _ ->
  runDotNetCommand "restore" $"\"{migProjectPath}\""
  runDotNetCommand "restore" $"\"{schemaProjectPath}\""
  runDotNetCommand "restore" $"\"{exampleProjectPath}\"")

Target.create "BuildSchema" (fun _ -> runDotNetCommand "build" $"\"{schemaProjectPath}\" --no-restore")

Target.create "Codegen" (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- codegen -d \"{rootDir}\"")

Target.create "BuildExample" (fun _ -> runDotNetCommand "build" $"\"{exampleProjectPath}\" --no-restore")

Target.create "Init" (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- init -d \"{rootDir}\"")

Target.create "CreateLegacySource" (fun _ ->
  deleteIfExists (targetDbPath ())
  createLegacyDatabase ())

Target.create "Migrate" (fun _ -> runDotNetCommand "run" $"--project \"{migProjectPath}\" -- migrate -d \"{rootDir}\"")

Target.create "RunProgram" (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

Target.create "RunInitExample" (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

Target.create "RunMigrationExample" (fun _ -> runDotNetCommand "run" $"--project \"{exampleProjectPath}\" --no-build")

"Clean" ==> "Restore" ==> "BuildSchema" ==> "Codegen" ==> "BuildExample"

"BuildExample" ==> "Init" ==> "RunInitExample"

"BuildExample" ==> "CreateLegacySource" ==> "Migrate" ==> "RunMigrationExample"

Target.runOrDefault requestedTarget
