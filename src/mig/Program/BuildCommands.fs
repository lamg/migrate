namespace Mig

open Argu
open System
open System.IO
open MigLib.Build
open MigLib.Util
open ProgramArgs
open ProgramCommon
open ProgramResolution

module internal ProgramBuildCommands =
  let codegen (args: ParseResults<CodegenArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "codegen" (args.TryGetResult CodegenArgs.Dir)

        let! assemblyPath, schemaModuleName, generatedModuleName, outputPath =
          resolveCodegenInputs
            currentDirectory
            (args.TryGetResult CodegenArgs.Assembly)
            (args.TryGetResult CodegenArgs.Schema_Module)
            (args.TryGetResult CodegenArgs.Module)
            (args.TryGetResult CodegenArgs.Output)

        let schemaPath = defaultSchemaFsPathForCurrentDirectory currentDirectory
        let dbFileNamePrefix = DirectoryInfo(currentDirectory).Name

        let! report =
          runCodegenFromAssemblyModulePath generatedModuleName dbFileNamePrefix schemaPath assemblyPath schemaModuleName outputPath

        writeCodegenReport (printfn "%s") report
        return 0
      }

    finishCommand "codegen" result

  let init (args: ParseResults<InitArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "init" (args.TryGetResult InitArgs.Dir)

        let! assemblyPath, moduleName =
          resolveRequiredCompiledMode
            "init"
            currentDirectory
            (args.TryGetResult InitArgs.Assembly)
            (args.TryGetResult InitArgs.Module)

        let! report =
          initDbFromAssemblyModulePath currentDirectory (args.TryGetResult InitArgs.Instance) assemblyPath moduleName
          |> fun task -> task.Result

        writeInitDbReport (printfn "%s") report
        return 0
      }

    finishCommand "init" result
