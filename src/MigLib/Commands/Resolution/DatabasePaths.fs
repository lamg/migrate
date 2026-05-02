module internal MigLib.Commands.Resolution.DatabasePaths

open System
open System.IO

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types
open MigLib.Util

let private regularError message = Error(MigError.Regular message)

let private isHexHashSegment (value: string) =
  value.Length = 16 && (value |> Seq.forall Uri.IsHexDigit)

let private validateFileSegment (label: string) (value: string) =
  let trimmed = value.Trim()

  if String.IsNullOrWhiteSpace trimmed then
    Error(MigError.Regular $"Database {label} is empty.")
  elif trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 then
    Error(MigError.Regular $"Database {label} '{trimmed}' contains invalid file name characters.")
  else
    Ok trimmed

let private resolveDbDirectory (project: MigProject) =
  let dbDir = project.dbDir

  if String.IsNullOrWhiteSpace dbDir then
    regularError "Database directory is empty."
  else
    let fullDbDir = Path.GetFullPath dbDir

    if Directory.Exists fullDbDir then
      Ok fullDbDir
    else
      regularError $"Database directory was not found: {fullDbDir}"

let private buildSchemaBoundDbFileName (project: MigProject) =
  result {
    let! dbApp = validateFileSegment "app" project.dbApp
    let! dbInstance = validateFileSegment "instance" project.dbInstance
    let! schemaHash = validateFileSegment "schema hash" project.schemaIdentity.schemaHash

    if isHexHashSegment schemaHash then
      return $"{dbApp}-{dbInstance}-{schemaHash}.sqlite"
    else
      return! regularError $"Database schema hash '{schemaHash}' must be exactly 16 hexadecimal characters."
  }

let private tryParseSchemaBoundDbFileName (dbApp: string) (dbInstance: string) (path: string) =
  let fileName = Path.GetFileName path

  if not (fileName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) then
    None
  else
    let expectedPrefix = $"{dbApp}-{dbInstance}-"
    let fileStem = Path.GetFileNameWithoutExtension fileName

    if not (fileStem.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) then
      None
    else
      let hashSegment = fileStem.Substring expectedPrefix.Length

      if isHexHashSegment hashSegment then
        Some hashSegment
      else
        None

let private isSamePath (left: string) (right: string) =
  String.Equals(Path.GetFullPath left, Path.GetFullPath right, StringComparison.OrdinalIgnoreCase)

let resolveTargetDbPath (project: MigProject) : Result<string, MigError> =
  result {
    let! fullDbDir = resolveDbDirectory project
    let! dbFileName = buildSchemaBoundDbFileName project
    return Path.Combine(fullDbDir, dbFileName)
  }

let resolveSourceDbPath (project: MigProject) : Result<string option, MigError> =
  result {
    let! fullDbDir = resolveDbDirectory project
    let! targetDbPath = resolveTargetDbPath project
    let! dbApp = validateFileSegment "app" project.dbApp
    let! dbInstance = validateFileSegment "instance" project.dbInstance

    let candidates =
      Directory.GetFiles(fullDbDir, "*.sqlite")
      |> Array.filter (fun path -> not (isSamePath path targetDbPath))
      |> Array.filter (fun path -> tryParseSchemaBoundDbFileName dbApp dbInstance path |> Option.isSome)
      |> Array.sort

    match candidates with
    | [||] -> return None
    | [| path |] -> return Some(Path.GetFullPath path)
    | many ->
      let candidateList = many |> Array.map Path.GetFullPath |> String.concat ", "

      return!
        regularError
          $"Could not infer source database automatically. Found multiple candidates matching '{dbApp}-{dbInstance}-<old-hash>.sqlite': {candidateList}."
  }

let resolveDatabasePaths (project: MigProject) : Result<ResolvedDatabasePaths, MigError> =
  result {
    let! fullDbDir = resolveDbDirectory project
    let! targetDbPath = resolveTargetDbPath project
    let! sourceDbPath = resolveSourceDbPath project

    return
      { targetDbPath = targetDbPath
        sourceDbPath = sourceDbPath
        archiveDirectory = Path.Combine(fullDbDir, "archive") }
  }
