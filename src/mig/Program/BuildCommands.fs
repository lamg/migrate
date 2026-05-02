namespace Mig

open Argu
open MigLib
open MigLib.Util
open ProgramArgs
open ProgramCommon
open ProgramResolution

module internal ProgramBuildCommands =
  let codegen (args: ParseResults<CodegenArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "codegen" (args.TryGetResult CodegenArgs.Dir)
        let! project = createMigProject "codegen" currentDirectory None

        let! codegenResult = MigLib.codegen project |> Result.mapError formatMigError

        printfn "Codegen complete."
        printfn $"Output path: {codegenResult.outputPath}"
        printfn $"Generated module: {codegenResult.generatedModuleName}"

        match codegenResult.generatedFiles with
        | [] -> ()
        | files ->
          printfn "Generated files:"
          files |> List.iter (fun path -> printfn $"  - {path}")

        return 0
      }

    finishCommand "codegen" result

  let init (args: ParseResults<InitArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "init" (args.TryGetResult InitArgs.Dir)
        let! project = createMigProject "init" currentDirectory (args.TryGetResult InitArgs.Instance)

        let! initResult =
          MigLib.init project
          |> fun task -> task.Result
          |> Result.mapError formatMigError

        printfn "Init complete."
        printfn $"Database: {initResult.newDbPath}"
        printfn $"Seeded rows: {initResult.seededRows}"
        return 0
      }

    finishCommand "init" result
