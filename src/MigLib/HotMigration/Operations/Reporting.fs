namespace Mig

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open MigLib.Util
open DeclarativeMigrations.DataCopy
open DeclarativeMigrations.SchemaDiff
open DeclarativeMigrations.Types
open Mig.HotMigrationTypes
open Mig.HotMigrationPrimitives
open Mig.HotMigrationMetadata
open Mig.HotMigrationSchemaIntrospection
open Mig.HotMigrationPlanning
open Mig.HotMigrationShared

module internal HotMigrationReporting =
  let analyzeNonTableConsistency = HotMigrationPlanning.analyzeNonTableConsistency

  let getMigratePlanWithSchema
    (oldDbPath: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (newDbPath: string)
    : Task<Result<MigratePlanReport, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        else
          use oldConnection = openSqliteConnection oldDbPath

          let! sourceSchema = loadSchemaFromDatabase oldConnection migrationTables
          let nonTableConsistency = analyzeNonTableConsistency targetSchema
          let schemaPlanResult = buildSchemaCopyPlan sourceSchema targetSchema

          let tableDifferences =
            match schemaPlanResult with
            | Ok schemaPlan -> describeSupportedDifferences schemaPlan
            | Error _ -> []

          let supportedDifferences = tableDifferences @ nonTableConsistency.supportedLines

          let plannerResult = buildBulkCopyPlan sourceSchema targetSchema

          let plannedCopyTargets, plannerUnsupported =
            match plannerResult with
            | Ok plan -> plan.steps |> List.map _.mapping.targetTable, []
            | Error message -> [], [ message ]

          let schemaPlanUnsupported =
            match schemaPlanResult with
            | Ok _ -> []
            | Error message -> [ message ]

          let unsupportedDifferences =
            nonTableConsistency.unsupportedLines
            @ schemaPlanUnsupported
            @ plannerUnsupported

          let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
          let! oldMigrationLogTablePresent = tableExists oldConnection None "_migration_log"
          let! oldMigrationLogEntries = countRowsIfTableExists oldConnection None "_migration_log"

          let markerPrerequisite =
            match oldMarkerStatus with
            | Some status -> $"_migration_marker is present with status '{status}' (migrate will set it to recording)."
            | None -> "_migration_marker is absent (migrate will create it in recording mode)."

          let logPrerequisite =
            if oldMigrationLogTablePresent then
              $"_migration_log is present with {oldMigrationLogEntries} entries (migrate will recreate it)."
            else
              "_migration_log is absent (migrate will create it)."

          let newDatabaseAlreadyExists = File.Exists newDbPath

          let targetPrerequisite =
            if newDatabaseAlreadyExists then
              $"target database already exists: {newDbPath}"
            else
              $"target database path is available: {newDbPath}"

          let driftPrerequisite =
            if unsupportedDifferences.IsEmpty then
              "schema preflight checks pass."
            else
              $"schema preflight has {unsupportedDifferences.Length} blocking issue(s)."

          let replayPrerequisites =
            [ markerPrerequisite; logPrerequisite; targetPrerequisite; driftPrerequisite ]

          let canRunMigrate = not newDatabaseAlreadyExists && unsupportedDifferences.IsEmpty

          return
            { schemaHash = schemaIdentity.schemaHash
              schemaCommit = schemaIdentity.schemaCommit
              supportedDifferences = supportedDifferences
              unsupportedDifferences = unsupportedDifferences
              plannedCopyTargets = plannedCopyTargets
              replayPrerequisites = replayPrerequisites
              canRunMigrate = canRunMigrate }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let getStatus (oldDbPath: string) (newDbPath: string option) : Task<Result<MigrationStatusReport, SqliteException>> =
    task {
      try
        use oldConnection = openSqliteConnection oldDbPath

        let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
        let! migrationLogEntries = countRowsIfTableExists oldConnection None "_migration_log"

        match newDbPath with
        | None ->
          return
            Ok
              { oldMarkerStatus = oldMarkerStatus
                migrationLogEntries = migrationLogEntries
                pendingReplayEntries = None
                idMappingEntries = None
                newMigrationStatus = None
                idMappingTablePresent = None
                migrationProgressTablePresent = None
                schemaIdentityHash = None
                schemaIdentityCommit = None }
        | Some newPath ->
          use newConnection = openSqliteConnection newPath

          let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

          let! idMappingEntries =
            if idMappingTablePresent then
              countRows newConnection None "_id_mapping"
            else
              Task.FromResult 0L

          let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
          let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
          let! progress = readMigrationProgress newConnection None
          let! schemaIdentity = readSchemaIdentity newConnection None

          let isReady =
            newMigrationStatus
            |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

          let! pendingReplayEntries =
            if isReady then
              Task.FromResult 0L
            else
              match progress with
              | Some row -> countPendingLogEntries oldConnection None row.lastReplayedLogId
              | None -> Task.FromResult migrationLogEntries

          return
            Ok
              { oldMarkerStatus = oldMarkerStatus
                migrationLogEntries = migrationLogEntries
                pendingReplayEntries = Some pendingReplayEntries
                idMappingEntries = Some idMappingEntries
                newMigrationStatus = newMigrationStatus
                idMappingTablePresent = Some idMappingTablePresent
                migrationProgressTablePresent = Some migrationProgressTablePresent
                schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
                schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let getNewDatabaseStatus (newDbPath: string) : Task<Result<NewDatabaseStatusReport, SqliteException>> =
    task {
      try
        use newConnection = openSqliteConnection newDbPath

        let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

        let! idMappingEntries =
          if idMappingTablePresent then
            countRows newConnection None "_id_mapping"
          else
            Task.FromResult 0L

        let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
        let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
        let! schemaIdentity = readSchemaIdentity newConnection None

        return
          Ok
            { newMigrationStatus = newMigrationStatus
              idMappingEntries = idMappingEntries
              idMappingTablePresent = idMappingTablePresent
              migrationProgressTablePresent = migrationProgressTablePresent
              schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
              schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let getOldDatabaseStatus (oldDbPath: string) : Task<Result<OldDatabaseStatusReport, SqliteException>> =
    task {
      try
        use oldConnection = openSqliteConnection oldDbPath

        let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
        let! migrationLogTablePresent = tableExists oldConnection None "_migration_log"

        let! migrationLogEntries =
          if migrationLogTablePresent then
            countRows oldConnection None "_migration_log"
          else
            Task.FromResult 0L

        return
          Ok
            { oldMarkerStatus = oldMarkerStatus
              migrationLogEntries = migrationLogEntries
              migrationLogTablePresent = migrationLogTablePresent }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }
