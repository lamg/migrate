module Mig.Program

open Argu

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | [<Mandatory>] Old of path: string
  | [<Mandatory>] Schema of path: string
  | New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | Schema _ -> "path to the .fsx schema file"
      | New _ -> "path for the new database (default: <old>.new)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type DrainArgs =
  | [<Mandatory>] Old of path: string
  | [<Mandatory>] New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | New _ -> "path to the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CutoverArgs =
  | [<Mandatory>] New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | New _ -> "path to the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type StatusArgs =
  | [<Mandatory>] Old of path: string
  | New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | New _ -> "path to the new database"

type Command =
  | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
  | [<CliPrefix(CliPrefix.None)>] Drain of ParseResults<DrainArgs>
  | [<CliPrefix(CliPrefix.None)>] Cutover of ParseResults<CutoverArgs>
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Migrate _ -> "create new database and copy data from old"
      | Drain _ -> "stop writes on old database and replay accumulated changes"
      | Cutover _ -> "mark new database as ready for serving"
      | Status _ -> "show current migration state"

let migrate (args: ParseResults<MigrateArgs>) =
  let old = args.GetResult MigrateArgs.Old
  let schema = args.GetResult MigrateArgs.Schema
  let newDb = args.TryGetResult MigrateArgs.New |> Option.defaultValue $"{old}.new"
  printfn $"migrate: not implemented (old={old}, schema={schema}, new={newDb})"
  0

let drain (args: ParseResults<DrainArgs>) =
  let old = args.GetResult DrainArgs.Old
  let newDb = args.GetResult DrainArgs.New
  printfn $"drain: not implemented (old={old}, new={newDb})"
  0

let cutover (args: ParseResults<CutoverArgs>) =
  let newDb = args.GetResult CutoverArgs.New
  printfn $"cutover: not implemented (new={newDb})"
  0

let status (args: ParseResults<StatusArgs>) =
  let old = args.GetResult StatusArgs.Old
  let newDb = args.TryGetResult StatusArgs.New
  printfn $"status: not implemented (old={old}, new={newDb})"
  0

[<EntryPoint>]
let main argv =
  let parser = ArgumentParser.Create<Command>(programName = "mig")

  try
    let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

    match results.GetSubCommand() with
    | Migrate args -> migrate args
    | Drain args -> drain args
    | Cutover args -> cutover args
    | Status args -> status args
  with :? ArguParseException as ex ->
    printfn $"%s{ex.Message}"
    1
