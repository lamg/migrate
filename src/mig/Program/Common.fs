namespace Mig

open System
open MigLib.CompiledSchema
open ProgramArgs

module internal ProgramCommon =
  type ResolvedCompiledModule =
    { assemblyPath: string
      moduleName: string
      generatedModule: GeneratedSchemaModule
      newDbPath: string }

  type SchemaBoundDbPath =
    { schemaSourcePath: string
      path: string }

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

  let finishCommand (commandName: string) (result: Result<int, string>) =
    match result with
    | Ok exitCode -> exitCode
    | Error message ->
      eprintfn $"{commandName} failed: {message}"
      1
