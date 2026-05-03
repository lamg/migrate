namespace Mig

open Argu
open MigLib
open MigLib.Schema.Types
open MigLib.TaskResult
open ProgramArgs
open ProgramCommon

module internal ProgramBuildCommands =
  let codegen (args: ParseResults<CodegenArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCliDirectory (args.TryGetResult CodegenArgs.Dir)

        let project: MigProject =
          { dbInstance = "main"
            dbDir = currentDirectory
            targetSchema = emptyFile
            dbApp = ""
            schemaIdentity =
              { schemaHash = "0000000000000000"
                schemaCommit = None } }

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
        let! project = resolveCliProject (args.TryGetResult InitArgs.Dir) (args.TryGetResult InitArgs.Instance)

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
