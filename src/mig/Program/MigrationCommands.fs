namespace Mig

open Argu
open System
open System.IO
open MigLib.CompiledSchema
open MigLib.Util
open ProgramArgs
open ProgramCommon
open ProgramResolution
open Mig.HotMigration

module internal ProgramMigrationCommands =
  let private printMigrateRecoveryGuidance (oldDbPath: string) (newDbPath: string) =
    let oldSnapshot = getOldDatabaseStatus oldDbPath |> fun t -> t.Result
    let newDbPresent = File.Exists newDbPath

    let newSnapshot =
      if newDbPresent then
        Some(getNewDatabaseStatus newDbPath |> fun t -> t.Result)
      else
        None

    eprintfn "Recovery snapshot:"

    match oldSnapshot with
    | Ok report ->
      let oldMarkerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"

      let oldMigrationLogState =
        if report.migrationLogTablePresent then
          $"present ({report.migrationLogEntries} entries)"
        else
          "absent"

      eprintfn $"  Old marker status: {oldMarkerStatus}"
      eprintfn $"  Old _migration_log: {oldMigrationLogState}"
    | Error ex -> eprintfn $"  Old database snapshot unavailable: {formatExceptionDetails ex}"

    let newDbState = if newDbPresent then "present" else "absent"
    eprintfn $"  New database file: {newDbState} ({newDbPath})"

    match newSnapshot with
    | Some(Ok report) ->
      let newStatus = report.newMigrationStatus |> Option.defaultValue "no status marker"

      let idMappingState =
        if report.idMappingTablePresent then
          $"present ({report.idMappingEntries} entries)"
        else
          "absent"

      let migrationProgressState =
        if report.migrationProgressTablePresent then
          "present"
        else
          "absent"

      eprintfn $"  New migration status: {newStatus}"
      eprintfn $"  New _id_mapping: {idMappingState}"
      eprintfn $"  New _migration_progress: {migrationProgressState}"
    | Some(Error ex) -> eprintfn $"  New database snapshot unavailable: {formatExceptionDetails ex}"
    | None -> ()

    let hasRecordingMarker =
      match oldSnapshot with
      | Ok report ->
        report.oldMarkerStatus
        |> Option.exists (fun status -> status.Equals("recording", StringComparison.OrdinalIgnoreCase))
      | Error _ -> false

    let hasOldMigrationLog =
      match oldSnapshot with
      | Ok report -> report.migrationLogTablePresent
      | Error _ -> false

    let safeImmediateRerun =
      not newDbPresent && not hasRecordingMarker && not hasOldMigrationLog

    let guidance = ResizeArray<string>()
    guidance.Add "Keep the old database as source of truth; do not run drain/cutover after a failed migrate."

    if hasRecordingMarker then
      guidance.Add "Old marker is recording; new writes may be accumulating in _migration_log."

    if hasOldMigrationLog || hasRecordingMarker then
      guidance.Add "If restarting from scratch: stop writes first, then clear _migration_marker and _migration_log."

    if newDbPresent then
      guidance.Add $"Delete failed target database before rerun: {newDbPath}."
    else
      guidance.Add "No target database file was created."

    guidance.Add "Run `mig plan` to confirm inferred paths and preflight status."

    if safeImmediateRerun then
      guidance.Add "Current snapshot indicates immediate rerun is safe."
    else
      guidance.Add "Rerun `mig migrate` only after cleanup/reset conditions above are satisfied."

    eprintfn "Recovery guidance:"
    guidance |> Seq.iteri (fun index line -> eprintfn $"  {index + 1}. {line}")

  let migrate (args: ParseResults<MigrateArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir)
        let instance = args.TryGetResult MigrateArgs.Instance

        let! compiledModule =
          resolveRequiredCompiledModuleForCommand
            "migrate"
            currentDirectory
            instance
            (args.TryGetResult MigrateArgs.Assembly)
            (args.TryGetResult MigrateArgs.Module)

        let! dbApp =
          compiledModule.generatedModule.dbApp
          |> ResultEx.requireSome "Compiled generated module does not define DbApp for `migrate`."

        let newDb = compiledModule.newDbPath
        let! sourceDb = resolveMigrationSourceDb currentDirectory dbApp instance newDb

        match sourceDb with
        | None ->
          printfn "Migrate skipped."
          printCompiledModuleInfo compiledModule
          printfn $"Database already present for current schema: {newDb}"
          return 0
        | Some old ->
          let! migrateResult =
            runMigrateFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
            |> fun t -> t.Result
            |> Result.mapError formatExceptionDetails

          printfn "Migrate complete."
          printfn $"Old database: {old}"
          printCompiledModuleInfo compiledModule
          printfn $"New database: {migrateResult.newDbPath}"
          printfn $"Copied tables: {migrateResult.copiedTables}"
          printfn $"Copied rows: {migrateResult.copiedRows}"
          return 0
      }

    match result with
    | Error message ->
      let currentDirectoryResult =
        resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir)

      match currentDirectoryResult with
      | Ok currentDirectory ->
        let instance = args.TryGetResult MigrateArgs.Instance

        match
          resolveRequiredCompiledModuleForCommand
            "migrate"
            currentDirectory
            instance
            (args.TryGetResult MigrateArgs.Assembly)
            (args.TryGetResult MigrateArgs.Module)
        with
        | Ok compiledModule ->
          let newDb = compiledModule.newDbPath

          match compiledModule.generatedModule.dbApp with
          | Some dbApp ->
            match resolveMigrationSourceDb currentDirectory dbApp instance newDb with
            | Ok(Some old) ->
              printMigrateRecoveryGuidance old newDb
              finishCommand "migrate" (Error message)
            | _ -> finishCommand "migrate" (Error message)
          | None -> finishCommand "migrate" (Error message)
        | Error _ -> finishCommand "migrate" (Error message)
      | Error _ -> finishCommand "migrate" (Error message)
    | Ok exitCode -> exitCode

  let offline (args: ParseResults<OfflineArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "offline" (args.TryGetResult OfflineArgs.Dir)
        let instance = args.TryGetResult OfflineArgs.Instance

        let! compiledModule =
          resolveRequiredCompiledModuleForCommand
            "offline"
            currentDirectory
            instance
            (args.TryGetResult OfflineArgs.Assembly)
            (args.TryGetResult OfflineArgs.Module)

        let! dbApp =
          compiledModule.generatedModule.dbApp
          |> ResultEx.requireSome "Compiled generated module does not define DbApp for `offline`."

        let newDb = compiledModule.newDbPath
        let! sourceDb = resolveMigrationSourceDb currentDirectory dbApp instance newDb

        match sourceDb with
        | None ->
          printfn "Offline migration skipped."
          printCompiledModuleInfo compiledModule
          printfn $"Database already present for current schema: {newDb}"
          return 0
        | Some old ->
          let! migrateResult =
            runOfflineMigrateFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
            |> _.Result
            |> Result.mapError formatExceptionDetails

          let! cleanupResult =
            runArchiveOld currentDirectory old
            |> _.Result
            |> Result.mapError (fun ex -> $"after creating new database: {formatExceptionDetails ex}")

          let previousMarkerStatus =
            cleanupResult.previousMarkerStatus |> Option.defaultValue "no marker"

          let replacedExistingArchive =
            if cleanupResult.replacedExistingArchive then
              "yes"
            else
              "no"

          printfn "Offline migration complete."
          printfn $"Old database: {old}"
          printCompiledModuleInfo compiledModule
          printfn $"New database: {migrateResult.newDbPath}"
          printfn $"Copied tables: {migrateResult.copiedTables}"
          printfn $"Copied rows: {migrateResult.copiedRows}"
          printfn $"Previous old marker status: {previousMarkerStatus}"
          printfn $"Archived database: {cleanupResult.archivePath}"
          printfn $"Replaced existing archive: {replacedExistingArchive}"
          printfn "Hot-migration tables were not created."
          return 0
      }

    finishCommand "offline" result

  let plan (args: ParseResults<PlanArgs>) =
    let printLines header lines =
      printfn $"{header}"

      match lines with
      | [] -> printfn "  - none"
      | values -> values |> List.iter (fun line -> printfn $"  - {line}")

    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "plan" (args.TryGetResult PlanArgs.Dir)
        let instance = args.TryGetResult PlanArgs.Instance

        let! compiledModule =
          resolveRequiredCompiledModuleForCommand
            "plan"
            currentDirectory
            instance
            (args.TryGetResult PlanArgs.Assembly)
            (args.TryGetResult PlanArgs.Module)

        let! dbApp =
          compiledModule.generatedModule.dbApp
          |> ResultEx.requireSome "Compiled generated module does not define DbApp for `plan`."

        let newDb = compiledModule.newDbPath
        let! sourceDb = resolveMigrationSourceDb currentDirectory dbApp instance newDb

        match sourceDb with
        | None ->
          printfn "Plan skipped."
          printCompiledModuleInfo compiledModule
          printfn $"Database already present for current schema: {newDb}"
          return 0
        | Some old ->
          let! report =
            getMigratePlanFromAssemblyPath compiledModule.assemblyPath compiledModule.moduleName old newDb
            |> fun t -> t.Result
            |> Result.mapError formatExceptionDetails

          let canRunMigrate = if report.canRunMigrate then "yes" else "no"

          printfn "Migration plan."
          printfn $"Old database: {old}"
          printCompiledModuleInfo compiledModule
          printfn $"Schema hash: {report.schemaHash}"

          match report.schemaCommit with
          | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
          | None -> ()

          printfn $"New database: {newDb}"
          printfn $"Can run migrate now: {canRunMigrate}"

          printLines "Planned copy targets (execution order):" report.plannedCopyTargets
          printLines "Supported differences:" report.supportedDifferences
          printLines "Unsupported differences:" report.unsupportedDifferences
          printLines "Replay prerequisites:" report.replayPrerequisites

          return if report.canRunMigrate then 0 else 1
      }

    finishCommand "plan" result

  let drain (args: ParseResults<DrainArgs>) =
    match resolveCommandDirectory "drain" (args.TryGetResult DrainArgs.Dir) with
    | Error message ->
      eprintfn $"drain failed: {message}"
      1
    | Ok currentDirectory ->
      let defaultAppName = DirectoryInfo(currentDirectory).Name
      let instance = args.TryGetResult DrainArgs.Instance

      let setupResult =
        result {
          let! compiledModule =
            resolveRequiredCompiledModuleForCommand
              "drain"
              currentDirectory
              instance
              (args.TryGetResult DrainArgs.Assembly)
              (args.TryGetResult DrainArgs.Module)

          let appName = compiledModule.generatedModule.dbApp |> Option.defaultValue defaultAppName
          let newDb = compiledModule.newDbPath

          let! old = inferOldDbWithExcludedTarget currentDirectory appName instance newDb
          return old, newDb
        }

      match setupResult with
      | Error message ->
        eprintfn $"drain failed: {message}"
        1
      | Ok(old, newDb) ->
        match runDrain old newDb |> fun t -> t.Result with
        | Ok result ->
          printfn "Drain complete."
          printfn $"Old database: {old}"
          printfn $"New database: {newDb}"
          printfn $"Replayed entries: {result.replayedEntries}"
          printfn $"Remaining log entries: {result.remainingEntries}"
          printfn "Run `mig cutover` when ready."
          0
        | Error ex ->
          eprintfn $"drain failed: {formatExceptionDetails ex}"
          1

  let cutover (args: ParseResults<CutoverArgs>) =
    let result =
      result {
        let! currentDirectory = resolveCommandDirectory "cutover" (args.TryGetResult CutoverArgs.Dir)
        let defaultAppName = DirectoryInfo(currentDirectory).Name
        let instance = args.TryGetResult CutoverArgs.Instance

        let! compiledModule =
          resolveRequiredCompiledModuleForCommand
            "cutover"
            currentDirectory
            instance
            (args.TryGetResult CutoverArgs.Assembly)
            (args.TryGetResult CutoverArgs.Module)

        let newDb = compiledModule.newDbPath
        let appName = compiledModule.generatedModule.dbApp |> Option.defaultValue defaultAppName

        let oldDb =
          inferOldDbFromCurrentDirectory currentDirectory appName instance (Some newDb)
          |> Result.toOption

        let! cutoverResult =
          match oldDb with
          | Some oldDb -> runCutoverWithOldSafety oldDb newDb |> fun t -> t.Result
          | None -> runCutover newDb |> fun t -> t.Result
          |> Result.mapError formatExceptionDetails

        let droppedIdMapping = if cutoverResult.idMappingDropped then "yes" else "no"

        let droppedMigrationProgress =
          if cutoverResult.migrationProgressDropped then
            "yes"
          else
            "no"

        printfn "Cutover complete."
        printfn $"New database: {newDb}"
        printfn $"Previous migration status: {cutoverResult.previousStatus}"
        printfn "Current migration status: ready"
        printfn $"Dropped _id_mapping: {droppedIdMapping}"
        printfn $"Dropped _migration_progress: {droppedMigrationProgress}"
        return 0
      }

    finishCommand "cutover" result

  let archiveOld (args: ParseResults<ArchiveOldArgs>) =
    match resolveCommandDirectory "archive-old" (args.TryGetResult ArchiveOldArgs.Dir) with
    | Error message ->
      eprintfn $"archive-old failed: {message}"
      1
    | Ok currentDirectory ->
      let defaultAppName = DirectoryInfo(currentDirectory).Name
      let instance = args.TryGetResult ArchiveOldArgs.Instance
      let compiledModule =
        match args.TryGetResult ArchiveOldArgs.Assembly with
        | Some _ ->
          resolveRequiredCompiledModuleForCommand
            "archive-old"
            currentDirectory
            instance
            (args.TryGetResult ArchiveOldArgs.Assembly)
            (args.TryGetResult ArchiveOldArgs.Module)
          |> Result.toOption
        | None -> None

      let appName = compiledModule |> Option.bind (fun item -> item.generatedModule.dbApp) |> Option.defaultValue defaultAppName

      let setupResult =
        result {
          let! inferredNew =
            resolveOptionalCompiledModeTargetDbPathForCommand
              "archive-old"
              currentDirectory
              instance
              (args.TryGetResult ArchiveOldArgs.Assembly)
              (args.TryGetResult ArchiveOldArgs.Module)

          let! old =
            inferOldDbFromCurrentDirectory currentDirectory appName instance inferredNew
            |> Result.mapError (fun message ->
              match inferredNew with
              | Some inferredTarget ->
                $"{message} Excluding inferred target '{inferredTarget}'. Use `-d` to select a different directory."
              | None -> $"{message} Use `-d` to select a different directory.")

          return old
        }

      match setupResult with
      | Error message ->
        eprintfn $"archive-old failed: {message}"
        1
      | Ok old ->
        match runArchiveOld currentDirectory old |> fun t -> t.Result with
        | Ok result ->
          let previousMarkerStatus =
            result.previousMarkerStatus |> Option.defaultValue "no marker"

          let replacedExistingArchive = if result.replacedExistingArchive then "yes" else "no"

          printfn "Old database archive complete."
          printfn $"Old database: {old}"
          printfn $"Previous marker status: {previousMarkerStatus}"
          printfn $"Archived database: {result.archivePath}"
          printfn $"Replaced existing archive: {replacedExistingArchive}"
          0
        | Error ex ->
          eprintfn $"archive-old failed: {formatExceptionDetails ex}"
          1

  let reset (args: ParseResults<ResetArgs>) =
    let isDryRun = args.Contains ResetArgs.Dry_Run

    match resolveCommandDirectory "reset" (args.TryGetResult ResetArgs.Dir) with
    | Error message ->
      eprintfn $"reset failed: {message}"
      1
    | Ok currentDirectory ->
      let defaultAppName = DirectoryInfo(currentDirectory).Name
      let instance = args.TryGetResult ResetArgs.Instance

      let setupResult =
        result {
          let! compiledModule =
            resolveRequiredCompiledModuleForCommand
              "reset"
              currentDirectory
              instance
              (args.TryGetResult ResetArgs.Assembly)
              (args.TryGetResult ResetArgs.Module)

          let appName = compiledModule.generatedModule.dbApp |> Option.defaultValue defaultAppName
          let newDb = compiledModule.newDbPath
          let! old = inferOldDbWithExcludedTarget currentDirectory appName instance newDb
          return old, newDb
        }

      match setupResult with
      | Error message ->
        eprintfn $"reset failed: {message}"
        1
      | Ok(old, newDb) ->
        if isDryRun then
          match getResetMigrationPlan old newDb |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"reset failed: {formatExceptionDetails ex}"
            1
          | Ok plan ->
            let previousOldMarkerStatus =
              plan.previousOldMarkerStatus |> Option.defaultValue "no marker"

            let wouldDropOldMarker = if plan.willDropOldMarker then "yes" else "no"
            let wouldDropOldLog = if plan.willDropOldLog then "yes" else "no"

            let previousNewStatus =
              plan.previousNewStatus |> Option.defaultValue "no status marker"

            let newDatabaseExisted = if plan.newDatabaseExisted then "yes" else "no"
            let wouldDeleteNewDatabase = if plan.willDeleteNewDatabase then "yes" else "no"
            let resetCanApply = if plan.canApplyReset then "yes" else "no"

            printfn "Migration reset dry run."
            printfn $"Old database: {old}"
            printfn $"Previous old marker status: {previousOldMarkerStatus}"
            printfn $"Would drop _migration_marker: {wouldDropOldMarker}"
            printfn $"Would drop _migration_log: {wouldDropOldLog}"
            printfn $"New database: {newDb}"
            printfn $"New database existed: {newDatabaseExisted}"

            if plan.newDatabaseExisted then
              printfn $"Previous new migration status: {previousNewStatus}"

            printfn $"Would delete new database: {wouldDeleteNewDatabase}"
            printfn $"Reset can be applied: {resetCanApply}"

            if not plan.canApplyReset then
              let blockedReason = plan.blockedReason |> Option.defaultValue "Reset is blocked."
              printfn $"Blocked reason: {blockedReason}"

            if plan.canApplyReset then 0 else 1
        else
          match runResetMigration old newDb |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"reset failed: {formatExceptionDetails ex}"
            1
          | Ok result ->
            let previousOldMarkerStatus =
              result.previousOldMarkerStatus |> Option.defaultValue "no marker"

            let droppedOldMarker = if result.oldMarkerDropped then "yes" else "no"
            let droppedOldLog = if result.oldLogDropped then "yes" else "no"

            let previousNewStatus =
              result.previousNewStatus |> Option.defaultValue "no status marker"

            let newDatabaseExisted = if result.newDatabaseExisted then "yes" else "no"
            let newDatabaseDeleted = if result.newDatabaseDeleted then "yes" else "no"

            printfn "Migration reset complete."
            printfn $"Old database: {old}"
            printfn $"Previous old marker status: {previousOldMarkerStatus}"
            printfn $"Dropped _migration_marker: {droppedOldMarker}"
            printfn $"Dropped _migration_log: {droppedOldLog}"
            printfn $"New database: {newDb}"
            printfn $"New database existed: {newDatabaseExisted}"

            if result.newDatabaseExisted then
              printfn $"Previous new migration status: {previousNewStatus}"

            printfn $"Deleted new database: {newDatabaseDeleted}"
            0

  let status (args: ParseResults<StatusArgs>) =
    match resolveCommandDirectory "status" (args.TryGetResult StatusArgs.Dir) with
    | Error message ->
      eprintfn $"status failed: {message}"
      1
    | Ok currentDirectory ->
      let defaultAppName = DirectoryInfo(currentDirectory).Name
      let instance = args.TryGetResult StatusArgs.Instance
      let compiledModule =
        match args.TryGetResult StatusArgs.Assembly with
        | Some _ ->
          resolveRequiredCompiledModuleForCommand
            "status"
            currentDirectory
            instance
            (args.TryGetResult StatusArgs.Assembly)
            (args.TryGetResult StatusArgs.Module)
          |> Result.toOption
        | None -> None

      let appName = compiledModule |> Option.bind (fun item -> item.generatedModule.dbApp) |> Option.defaultValue defaultAppName

      let setupResult =
        result {
          let! inferredNew =
            result {
              let! candidate =
                resolveOptionalCompiledModeTargetDbPathForCommand
                  "status"
                  currentDirectory
                  instance
                  (args.TryGetResult StatusArgs.Assembly)
                  (args.TryGetResult StatusArgs.Module)

              return candidate |> Option.filter File.Exists
            }

          return inferredNew
        }

      match setupResult with
      | Error message ->
        eprintfn $"status failed: {message}"
        1
      | Ok inferredNew ->
        let inferredOld =
          inferOldDbFromCurrentDirectory currentDirectory appName instance inferredNew

        match inferredOld, inferredNew with
        | Ok oldPath, _ ->
          match getStatus oldPath inferredNew |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"status failed: {formatExceptionDetails ex}"
            1
          | Ok report ->
            let markerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"
            printfn $"Old database: {oldPath}"
            printfn $"Marker status: {markerStatus}"
            printfn $"Migration log entries: {report.migrationLogEntries}"

            match inferredNew with
            | Some newPath ->
              let migrationStatus =
                report.newMigrationStatus |> Option.defaultValue "no status marker"

              let isReady =
                report.newMigrationStatus
                |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

              let pendingReplayText =
                match report.pendingReplayEntries with
                | Some pending when isReady -> $"{pending} (cutover complete)"
                | Some pending -> $"{pending}"
                | None -> "n/a"

              printfn $"New database: {newPath}"
              printfn $"Migration status: {migrationStatus}"

              match report.schemaIdentityHash with
              | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
              | None -> ()

              match report.schemaIdentityCommit with
              | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
              | None -> ()

              printfn $"Pending replay entries: {pendingReplayText}"

              match report.idMappingTablePresent, report.idMappingEntries with
              | Some false, _ -> printfn "_id_mapping: removed"
              | Some true, Some entries -> printfn $"_id_mapping entries: {entries}"
              | _ -> ()

              match report.migrationProgressTablePresent with
              | Some false -> printfn "_migration_progress: removed"
              | Some true -> printfn "_migration_progress: present"
              | None -> ()
            | None -> ()

            0
        | Error _, Some newPath ->
          match getNewDatabaseStatus newPath |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"status failed: {formatExceptionDetails ex}"
            1
          | Ok report ->
            let migrationStatus =
              report.newMigrationStatus |> Option.defaultValue "no status marker"

            printfn "Old database: n/a (not inferred)"
            printfn "Marker status: n/a"
            printfn "Migration log entries: n/a"
            printfn $"New database: {newPath}"
            printfn $"Migration status: {migrationStatus}"

            match report.schemaIdentityHash with
            | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
            | None -> ()

            match report.schemaIdentityCommit with
            | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
            | None -> ()

            printfn "Pending replay entries: n/a (old database unavailable)"

            match report.idMappingTablePresent with
            | false -> printfn "_id_mapping: removed"
            | true -> printfn $"_id_mapping entries: {report.idMappingEntries}"

            match report.migrationProgressTablePresent with
            | false -> printfn "_migration_progress: removed"
            | true -> printfn "_migration_progress: present"

            0
        | Error message, None ->
          eprintfn $"status failed: {message} Use `-d` to select a different directory."
          1
