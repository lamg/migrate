module Test.Cli.ProgramTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit

let private cliIoLock = obj ()

let private runMigCliInDirectory (workingDirectory: string option) (args: string list) =
  lock cliIoLock (fun () ->
    let originalOut = Console.Out
    let originalErr = Console.Error
    let originalDirectory = Directory.GetCurrentDirectory()
    use outWriter = new StringWriter()
    use errWriter = new StringWriter()
    Console.SetOut outWriter
    Console.SetError errWriter

    try
      match workingDirectory with
      | Some dir -> Directory.SetCurrentDirectory dir
      | None -> ()

      let exitCode = Mig.Program.main (args |> List.toArray)
      exitCode, outWriter.ToString(), errWriter.ToString()
    finally
      Directory.SetCurrentDirectory originalDirectory
      Console.SetOut originalOut
      Console.SetError originalErr)

let private assertCliHelpOutput (args: string list) (expectedUsage: string) (expectedFragments: string list) =
  let exitCode, stdOut, stdErr = runMigCliInDirectory None args
  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains(expectedUsage, stdOut)

  expectedFragments
  |> List.iter (fun fragment -> Assert.Contains(fragment, stdOut))

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")
  Directory.CreateDirectory path |> ignore
  path

let private writeFile (path: string) (text: string) =
  let directory = Path.GetDirectoryName path

  if not (String.IsNullOrWhiteSpace directory) then
    Directory.CreateDirectory directory |> ignore

  File.WriteAllText(path, text)

let private openConnection dbPath =
  SQLitePCL.Batteries_V2.Init()
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let private runtimeProjectPath tempDir = Path.Combine(tempDir, "Runtime.fsproj")

let private domainModelingProjectPath tempDir =
  Path.Combine(tempDir, "DomainModeling", "DomainModeling.fsproj")

let private targetDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-0123456789abcdef.sqlite")

let private sourceDbPath tempDir =
  Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

let private archivedSourceDbPath tempDir =
  Path.Combine(tempDir, "archive", "generated-fixture-main-fedcba9876543210.sqlite")

let private writeRuntimeLayout tempDir =
  let fixtureAssembly = typeof<TestGenerated.Db.Marker>.Assembly.Location
  let assemblyName = Path.GetFileNameWithoutExtension fixtureAssembly

  let runtimeAssemblyPath =
    Path.Combine(tempDir, "bin", "Debug", "net10.0", $"{assemblyName}.dll")

  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>"

  writeFile (domainModelingProjectPath tempDir) "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

  Directory.CreateDirectory(Path.GetDirectoryName runtimeAssemblyPath) |> ignore
  File.Copy(fixtureAssembly, runtimeAssemblyPath, true)

let private writeCodegenLayout tempDir =
  let fixtureAssembly = typeof<TestCodegenSchema.MigSchema.Marker>.Assembly.Location
  let assemblyName = Path.GetFileNameWithoutExtension fixtureAssembly

  let schemaAssemblyPath =
    Path.Combine(tempDir, "DomainModeling", "bin", "Debug", "net10.0", $"{assemblyName}.dll")

  let schemaSourcePath = Path.Combine(tempDir, "DomainModeling", "MigSchema.fs")

  writeFile
    (runtimeProjectPath tempDir)
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>CliGenerated</RootNamespace></PropertyGroup></Project>"

  writeFile
    (domainModelingProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>"

  writeFile
    schemaSourcePath
    "[<MigLib.Dsl.Attributes.GeneratedDbNamespace(\"TestGeneratedDb\")>]\nmodule TestCodegenSchema.MigSchema"

  Directory.CreateDirectory(Path.GetDirectoryName schemaAssemblyPath) |> ignore
  File.Copy(fixtureAssembly, schemaAssemblyPath, true)

[<Fact>]
let ``cli root help shows command-backed surface`` () =
  assertCliHelpOutput
    [ "--help" ]
    "USAGE: mig [--help] [--version] [<subcommand> [<options>]]"
    [ "init <options>"
      "codegen <options>"
      "migrate <options>"
      "plan <options>"
      "reset <options>"
      "status <options>" ]

[<Fact>]
let ``cli subcommand help shows command-backed options`` () =
  let cases =
    [ ([ "init"; "--help" ], "USAGE: mig init [--help] [--dir <path>] [--instance <name>]")
      ([ "migrate"; "--help" ], "USAGE: mig migrate [--help] [--dir <path>] [--instance <name>]")
      ([ "plan"; "--help" ], "USAGE: mig plan [--help] [--dir <path>] [--instance <name>]")
      ([ "reset"; "--help" ], "USAGE: mig reset [--help] [--dir <path>] [--instance <name>]")
      ([ "status"; "--help" ], "USAGE: mig status [--help] [--dir <path>] [--instance <name>]")
      ([ "codegen"; "--help" ], "USAGE: mig codegen [--help] [--dir <path>]") ]

  for args, expectedUsage in cases do
    assertCliHelpOutput args expectedUsage [ "--dir, -d <path>" ]

[<Fact>]
let ``cli init creates target database from runtime project convention`` () =
  let tempDir = createTempDir "mig_cli_init_commands"

  try
    writeRuntimeLayout tempDir

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "init" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains("Init complete.", stdOut)
    Assert.Contains(targetDbPath tempDir, stdOut)
    Assert.True(File.Exists(targetDbPath tempDir))
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``cli codegen generates Db fs from runtime and DomainModeling convention`` () =
  let tempDir = createTempDir "mig_cli_codegen_commands"

  try
    writeCodegenLayout tempDir
    let outputPath = Path.Combine(tempDir, "DomainModeling", "Db.fs")

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "codegen" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains("Codegen complete.", stdOut)
    Assert.Contains(outputPath, stdOut)
    Assert.True(File.Exists outputPath)

    let generated = File.ReadAllText outputPath
    Assert.Contains("module TestGeneratedDb.Db", generated)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status reports command-managed database state`` () =
  let tempDir = createTempDir "mig_cli_status_commands"

  try
    writeRuntimeLayout tempDir
    writeFile (sourceDbPath tempDir) ""
    writeFile (archivedSourceDbPath tempDir) ""

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "status" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains($"Current database: {sourceDbPath tempDir}", stdOut)
    Assert.Contains("Needs migration: yes", stdOut)
    Assert.Contains(archivedSourceDbPath tempDir, stdOut)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``cli plan reports command migration plan`` () =
  let tempDir = createTempDir "mig_cli_plan_commands"

  try
    writeRuntimeLayout tempDir
    writeFile (sourceDbPath tempDir) ""

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "plan" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains("Migration plan.", stdOut)
    Assert.Contains($"Source database: {sourceDbPath tempDir}", stdOut)
    Assert.Contains($"Target database: {targetDbPath tempDir}", stdOut)
    Assert.Contains("Can migrate: yes", stdOut)
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate copies rows and archives source database`` () =
  let tempDir = createTempDir "mig_cli_migrate_commands"

  try
    writeRuntimeLayout tempDir

    use sourceConnection = openConnection (sourceDbPath tempDir)

    use createTableCmd =
      new SqliteCommand("CREATE TABLE generated_fixture(id INTEGER NOT NULL);", sourceConnection)

    createTableCmd.ExecuteNonQuery() |> ignore

    use insertCmd =
      new SqliteCommand("INSERT INTO generated_fixture(id) VALUES (1), (2);", sourceConnection)

    insertCmd.ExecuteNonQuery() |> ignore
    sourceConnection.Close()

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "migrate" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains("Migrate complete.", stdOut)
    Assert.Contains(targetDbPath tempDir, stdOut)
    Assert.Contains(archivedSourceDbPath tempDir, stdOut)
    Assert.False(File.Exists(sourceDbPath tempDir))
    Assert.True(File.Exists(targetDbPath tempDir))
    Assert.True(File.Exists(archivedSourceDbPath tempDir))
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``cli reset restores latest archived database`` () =
  let tempDir = createTempDir "mig_cli_reset_commands"

  try
    writeRuntimeLayout tempDir
    writeFile (targetDbPath tempDir) "current"

    Directory.CreateDirectory(Path.GetDirectoryName(archivedSourceDbPath tempDir))
    |> ignore

    use archiveConnection = openConnection (archivedSourceDbPath tempDir)

    use createFixtureCmd =
      new SqliteCommand("CREATE TABLE generated_fixture(id INTEGER NOT NULL);", archiveConnection)

    createFixtureCmd.ExecuteNonQuery() |> ignore

    use createReadonlyCmd =
      new SqliteCommand(
        "CREATE TABLE _mig_readonly(id INTEGER PRIMARY KEY CHECK (id = 1), marked_utc TEXT NOT NULL);",
        archiveConnection
      )

    createReadonlyCmd.ExecuteNonQuery() |> ignore

    use insertReadonlyCmd =
      new SqliteCommand("INSERT INTO _mig_readonly(id, marked_utc) VALUES (1, 'now');", archiveConnection)

    insertReadonlyCmd.ExecuteNonQuery() |> ignore
    archiveConnection.Close()

    let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "reset" ]

    Assert.Equal(0, exitCode)
    Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
    Assert.Contains("Reset complete.", stdOut)
    Assert.Contains($"Removed current database: {targetDbPath tempDir}", stdOut)
    Assert.Contains($"Restored database: {sourceDbPath tempDir}", stdOut)
    Assert.False(File.Exists(targetDbPath tempDir))
    Assert.False(File.Exists(archivedSourceDbPath tempDir))
    Assert.True(File.Exists(sourceDbPath tempDir))
  finally
    Directory.Delete(tempDir, true)
