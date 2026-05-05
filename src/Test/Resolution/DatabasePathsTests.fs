module Test.Resolution.DatabasePathsTests

open System
open System.IO

open MigLib.Types
open MigLib.Resolution.DatabasePaths
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

let private assertRegularErrorContains expectedText result =
  match result with
  | Error(MigError.Regular message) -> Assert.Contains(expectedText, message)
  | Error error -> failwith $"Expected MigError.Regular, got: {error}"
  | Ok value -> failwith $"Expected error, got: {value}"

[<Fact>]
let ``buildSchemaBoundDbFileName builds schema-bound file name`` () =
  match buildSchemaBoundDbFileName "app" "tenant" "0123456789abcdef" with
  | Ok fileName -> Assert.Equal("app-tenant-0123456789abcdef.sqlite", fileName)
  | Error error -> failwith $"Expected file name to resolve, got: {error}"

[<Fact>]
let ``buildSchemaBoundDbFileName fails when dbInstance is empty`` () =
  buildSchemaBoundDbFileName "app" " " "0123456789abcdef"
  |> assertRegularErrorContains "Database instance is empty"

[<Fact>]
let ``buildSchemaBoundDbFileName fails when schema hash is not sixteen hex characters`` () =
  buildSchemaBoundDbFileName "app" "main" "not-a-hash"
  |> assertRegularErrorContains "must be exactly 16 hexadecimal characters"

[<Fact>]
let ``resolveDbDirectory fails when dbDir is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_resolve_target_missing_dir_{Guid.NewGuid()}")

  resolveDbDirectory tempDir
  |> assertRegularErrorContains "Database directory was not found"

[<Fact>]
let ``resolveSourceDbPath returns none when no old database exists`` () =
  let tempDir = createTempDir "mig_resolve_source_none"

  try
    match resolveSourceDbPath tempDir "app" "main" "0123456789abcdef" with
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

    match resolveSourceDbPath tempDir "app" "main" "0123456789abcdef" with
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

    match resolveSourceDbPath tempDir "app" "main" "0123456789abcdef" with
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

    resolveSourceDbPath tempDir "app" "main" "0123456789abcdef"
    |> assertRegularErrorContains "Found multiple candidates"
  finally
    Directory.Delete(tempDir, true)
