namespace Mig

open System
open System.IO
open MigLib
open MigLib.Types
open MigLib.TaskResult
open ProgramArgs

module internal ProgramCommon =
  let getVersionText () =
    let version = typeof<Command>.Assembly.GetName().Version

    if isNull version then
      "unknown"
    else
      $"{version.Major}.{version.Minor}.{version.Build}"

  let formatExceptionDetails (ex: exn) =
    let rec loop (current: exn) (acc: string list) =
      if isNull current then
        List.rev acc
      else
        let message =
          if String.IsNullOrWhiteSpace current.Message then
            "(no message)"
          else
            current.Message.Trim()

        let rendered = $"{current.GetType().FullName}: {message}"
        loop current.InnerException (rendered :: acc)

    let chain = loop ex [] |> String.concat " --> "
    let debugValue = Environment.GetEnvironmentVariable "MIG_DEBUG"

    if
      String.Equals(debugValue, "1", StringComparison.Ordinal)
      || String.Equals(debugValue, "true", StringComparison.OrdinalIgnoreCase)
    then
      $"{chain}{Environment.NewLine}{ex}"
    else
      chain

  let formatMigError error =
    match error with
    | MigError.Regular message -> message
    | MigError.Sqlite ex -> formatExceptionDetails ex
    | MigError.Other ex -> formatExceptionDetails ex

  let finishCommand (commandName: string) (result: Result<int, string>) =
    match result with
    | Ok exitCode -> exitCode
    | Error message ->
      eprintfn $"{commandName} failed: {message}"
      1

  let resolveCliDirectory (candidateDirectory: string option) : Result<string, string> =
    let targetDirectory =
      candidateDirectory
      |> Option.defaultValue (Directory.GetCurrentDirectory())
      |> Path.GetFullPath

    if Directory.Exists targetDirectory then
      Ok targetDirectory
    else
      Error $"Project discovery directory was not found: {targetDirectory}"

  let resolveCliProject
    (candidateDirectory: string option)
    (instance: string option)
    : Result<ResolvedProject, string> =
    result {
      let! targetDirectory = resolveCliDirectory candidateDirectory

      return!
        MigProject.Mig.discover targetDirectory instance targetDirectory
        |> fun task -> task.Result
        |> Result.mapError formatMigError
    }
