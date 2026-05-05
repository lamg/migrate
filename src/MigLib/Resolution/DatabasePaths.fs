module internal MigLib.Resolution.DatabasePaths

open System
open System.IO

open MigLib.Types
open MigLib.TaskResult

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

let resolveDbDirectory (dbDir: string) =
  if String.IsNullOrWhiteSpace dbDir then
    regularError "Database directory is empty."
  else
    let fullDbDir = Path.GetFullPath dbDir

    if Directory.Exists fullDbDir then
      Ok fullDbDir
    else
      regularError $"Database directory was not found: {fullDbDir}"

let buildSchemaBoundDbFileName app dbInstance hash =
  result {
    let! dbApp = validateFileSegment "app" app
    let! dbInstance = validateFileSegment "instance" dbInstance
    let! schemaHash = validateFileSegment "schema hash" hash

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

let resolveSourceDbPath
  (dbDir: string)
  (dbApp: string)
  (dbInstance: string)
  (targetSchemaHash: string)
  : Result<string option, MigError> =
  result {
    let! fullDbDir = resolveDbDirectory dbDir

    let candidates =
      Directory.GetFiles(fullDbDir, "*.sqlite")
      |> Array.filter (fun path ->
        match tryParseSchemaBoundDbFileName dbApp dbInstance path with
        | Some hash -> not (String.Equals(hash, targetSchemaHash, StringComparison.OrdinalIgnoreCase))
        | None -> false)
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
