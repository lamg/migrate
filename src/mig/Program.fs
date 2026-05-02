module Mig.Program

open Argu
open ProgramArgs
open ProgramCommon

type MigrateArgs = ProgramArgs.MigrateArgs
type InitArgs = ProgramArgs.InitArgs
type PlanArgs = ProgramArgs.PlanArgs
type ResetArgs = ProgramArgs.ResetArgs
type StatusArgs = ProgramArgs.StatusArgs
type CodegenArgs = ProgramArgs.CodegenArgs
type Command = ProgramArgs.Command

[<EntryPoint>]
let main argv =
  let parser = ArgumentParser.Create<Command>(programName = "mig")

  try
    let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

    if results.Contains Version then
      printfn $"{getVersionText ()}"
      0
    else
      match results.GetSubCommand() with
      | Init args -> ProgramBuildCommands.init args
      | Codegen args -> ProgramBuildCommands.codegen args
      | Migrate args -> ProgramMigrationCommands.migrate args
      | Plan args -> ProgramMigrationCommands.plan args
      | Reset args -> ProgramMigrationCommands.reset args
      | Status args -> ProgramMigrationCommands.status args
      | Version ->
        printfn $"{getVersionText ()}"
        0
  with :? ArguParseException as ex ->
    printfn $"%s{ex.Message}"
    1
