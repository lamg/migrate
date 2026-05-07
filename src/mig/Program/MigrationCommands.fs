namespace Mig

open Argu
open MigLib
open MigLib.TaskResult
open ProgramArgs
open ProgramCommon

module internal ProgramMigrationCommands =
  let private printLines header lines =
    printfn $"{header}"

    match lines with
    | [] -> printfn "  - none"
    | values -> values |> List.iter (fun line -> printfn $"  - {line}")

  let private reportProgress message = task { printfn "%s" message }

  let migrate (args: ParseResults<MigrateArgs>) =
    let result =
      result {
        let! project = resolveCliProject (args.TryGetResult MigrateArgs.Dir) (args.TryGetResult MigrateArgs.Instance)

        let! migrateResult =
          MigProject.Mig.migrate reportProgress project
          |> fun task -> task.Result
          |> Result.mapError formatMigError

        printfn "Migrate complete."
        printfn $"New database: {migrateResult.newDbPath}"
        printfn $"Copied tables: {migrateResult.copiedTables}"
        printfn $"Copied rows: {migrateResult.copiedRows}"

        match migrateResult.archivedOldDbPath with
        | Some archivedOldDbPath -> printfn $"Archived old database: {archivedOldDbPath}"
        | None -> ()

        return 0
      }

    finishCommand "migrate" result

  let plan (args: ParseResults<PlanArgs>) =
    let result =
      result {
        let! project = resolveCliProject (args.TryGetResult PlanArgs.Dir) (args.TryGetResult PlanArgs.Instance)

        let! planResult =
          MigProject.Mig.plan project
          |> (fun task -> task.Result)
          |> Result.mapError formatMigError

        printfn "Migration plan."

        match planResult.sourceDbPath with
        | Some sourceDbPath -> printfn $"Source database: {sourceDbPath}"
        | None -> printfn "Source database: none"

        let canMigrate = if planResult.canMigrate then "yes" else "no"
        printfn $"Target database: {planResult.targetDbPath}"
        printfn $"Can migrate: {canMigrate}"
        printLines "Supported differences:" planResult.supportedDifferences
        printLines "Unsupported differences:" planResult.unsupportedDifferences

        return if planResult.canMigrate then 0 else 1
      }

    finishCommand "plan" result

  let reset (args: ParseResults<ResetArgs>) =
    let result =
      result {
        let! project = resolveCliProject (args.TryGetResult ResetArgs.Dir) (args.TryGetResult ResetArgs.Instance)

        let! resetResult =
          MigProject.Mig.reset project
          |> (fun task -> task.Result)
          |> Result.mapError formatMigError

        printfn "Reset complete."

        match resetResult.removedCurrentDbPath with
        | Some removedCurrentDbPath -> printfn $"Removed current database: {removedCurrentDbPath}"
        | None -> printfn "Removed current database: none"

        match resetResult.restoredDbPath with
        | Some restoredDbPath -> printfn $"Restored database: {restoredDbPath}"
        | None -> printfn "Restored database: none"

        return 0
      }

    finishCommand "reset" result

  let status (args: ParseResults<StatusArgs>) =
    let result =
      result {
        let! project = resolveCliProject (args.TryGetResult StatusArgs.Dir) (args.TryGetResult StatusArgs.Instance)

        let! statusResult =
          MigProject.Mig.status project
          |> (fun task -> task.Result)
          |> Result.mapError formatMigError

        match statusResult.currentDbPath with
        | Some currentDbPath -> printfn $"Current database: {currentDbPath}"
        | None -> printfn "Current database: none"

        let needsMigration = if statusResult.needsMigration then "yes" else "no"
        printfn $"Needs migration: {needsMigration}"
        printLines "Archived databases:" statusResult.archivedDbPaths
        return 0
      }

    finishCommand "status" result
