module Test.Commands.Resolution.DatabasePathsTests

open System
open System.IO

open Mig.DeclarativeMigrations.Types
open Mig.HotMigration
open MigLib.Commands.Types
open MigLib.Commands.Resolution.DatabasePaths
open MigLib.Commands.Resolution.Types
open Xunit

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")

  Directory.CreateDirectory path |> ignore
  path

let private writeFile (path: string) (text: string) =
  let directory = Path.GetDirectoryName path

  if not (String.IsNullOrWhiteSpace directory) then
    Directory.CreateDirectory directory |> ignore

  File.WriteAllText(path, text)

let private emptySchema: SqlFile =
  { measureTypes = []
    inserts = []
    views = []
    tables = []
    indexes = []
    triggers = [] }

let private makeResolvedSchema dbDir dbInstance schemaHash =
  let runtimeProjectPath = Path.Combine(dbDir, "Runtime.fsproj")
  let schemaDirectory = Path.Combine(dbDir, "MigSchema")

  { assembly =
      { project =
          { migProject =
              { fsProject = runtimeProjectPath
                dbInstance = dbInstance
                dbDir = dbDir }
            runtimeProjectPath = runtimeProjectPath
            runtimeProjectDirectory = dbDir
            runtimeProjectName = "Runtime"
            schemaProjectPath = Path.Combine(schemaDirectory, "MigSchema.fsproj")
            schemaDirectory = schemaDirectory }
        assemblyName = "Runtime"
        assemblyPath = Path.Combine(dbDir, "Runtime.dll") }
    moduleName = "Runtime.Db"
    generatedModule =
      { schema = emptySchema
        schemaIdentity =
          { schemaHash = schemaHash
            schemaCommit = None }
        schemaHash = schemaHash
        dbApp = "app"
        defaultDbInstance = "main" } }

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``resolveTargetDbPath builds schema-bound path`` () =
  let tempDir = createTempDir "mig_resolve_target_db"

  try
    let schema = makeResolvedSchema tempDir "tenant" "0123456789abcdef"

    match resolveTargetDbPath schema with
    | Ok path -> Assert.Equal(Path.Combine(tempDir, "app-tenant-0123456789abcdef.sqlite"), path)
    | Error error -> failwith $"Expected target path to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveTargetDbPath fails when dbInstance is empty`` () =
  let tempDir = createTempDir "mig_resolve_target_empty_instance"

  try
    let schema = makeResolvedSchema tempDir " " "0123456789abcdef"

    resolveTargetDbPath schema
    |> assertRegularErrorContains "Database instance is empty"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveTargetDbPath fails when schema hash is not sixteen hex characters`` () =
  let tempDir = createTempDir "mig_resolve_target_invalid_hash"

  try
    let schema = makeResolvedSchema tempDir "main" "not-a-hash"

    resolveTargetDbPath schema
    |> assertRegularErrorContains "must be exactly 16 hexadecimal characters"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveTargetDbPath fails when dbDir is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_resolve_target_missing_dir_{Guid.NewGuid()}")

  let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

  resolveTargetDbPath schema
  |> assertRegularErrorContains "Database directory was not found"

[<Fact>]
let ``resolveSourceDbPath returns none when no old database exists`` () =
  let tempDir = createTempDir "mig_resolve_source_none"

  try
    let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

    match resolveSourceDbPath schema with
    | Ok None -> ()
    | Ok other -> failwith $"Expected no source database, got: {other}"
    | Error error -> failwith $"Expected source path to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSourceDbPath returns one matching old database`` () =
  let tempDir = createTempDir "mig_resolve_source_one"

  try
    let oldDbPath = Path.Combine(tempDir, "app-main-fedcba9876543210.sqlite")
    writeFile oldDbPath ""
    writeFile (Path.Combine(tempDir, "other-main-1111111111111111.sqlite")) ""

    let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

    match resolveSourceDbPath schema with
    | Ok(Some path) -> Assert.Equal(oldDbPath, path)
    | Ok None -> failwith "Expected source database to resolve."
    | Error error -> failwith $"Expected source path to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSourceDbPath excludes target database`` () =
  let tempDir = createTempDir "mig_resolve_source_excludes_target"

  try
    writeFile (Path.Combine(tempDir, "app-main-0123456789abcdef.sqlite")) ""

    let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

    match resolveSourceDbPath schema with
    | Ok None -> ()
    | Ok other -> failwith $"Expected no source database, got: {other}"
    | Error error -> failwith $"Expected source path to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveSourceDbPath fails when multiple old databases match`` () =
  let tempDir = createTempDir "mig_resolve_source_multiple"

  try
    writeFile (Path.Combine(tempDir, "app-main-1111111111111111.sqlite")) ""
    writeFile (Path.Combine(tempDir, "app-main-2222222222222222.sqlite")) ""

    let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

    resolveSourceDbPath schema
    |> assertRegularErrorContains "Found multiple candidates"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveDatabasePaths includes archive directory`` () =
  let tempDir = createTempDir "mig_resolve_database_paths"

  try
    let schema = makeResolvedSchema tempDir "main" "0123456789abcdef"

    match resolveDatabasePaths schema with
    | Ok paths ->
      Assert.Equal(Path.Combine(tempDir, "app-main-0123456789abcdef.sqlite"), paths.targetDbPath)
      Assert.True(paths.sourceDbPath.IsNone)
      Assert.Equal(Path.Combine(tempDir, "archive"), paths.archiveDirectory)
    | Error error -> failwith $"Expected database paths to resolve, got: {error}"
  finally
    Directory.Delete(tempDir, true)
