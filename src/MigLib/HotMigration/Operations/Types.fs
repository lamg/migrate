namespace Mig

module HotMigrationTypes =
  type MigrationStatusReport =
    { oldMarkerStatus: string option
      migrationLogEntries: int64
      pendingReplayEntries: int64 option
      idMappingEntries: int64 option
      newMigrationStatus: string option
      idMappingTablePresent: bool option
      migrationProgressTablePresent: bool option
      schemaIdentityHash: string option
      schemaIdentityCommit: string option }

  type OldDatabaseStatusReport =
    { oldMarkerStatus: string option
      migrationLogEntries: int64
      migrationLogTablePresent: bool }

  type NewDatabaseStatusReport =
    { newMigrationStatus: string option
      idMappingEntries: int64
      idMappingTablePresent: bool
      migrationProgressTablePresent: bool
      schemaIdentityHash: string option
      schemaIdentityCommit: string option }

  type CutoverResult =
    { previousStatus: string
      idMappingDropped: bool
      migrationProgressDropped: bool }

  type MigrateResult =
    { newDbPath: string
      copiedTables: int
      copiedRows: int64 }

  type InitResult =
    { newDbPath: string; seededRows: int64 }

  type SchemaIdentity =
    { schemaHash: string
      schemaCommit: string option }

  type MigratePlanReport =
    { schemaHash: string
      schemaCommit: string option
      supportedDifferences: string list
      unsupportedDifferences: string list
      plannedCopyTargets: string list
      replayPrerequisites: string list
      canRunMigrate: bool }

  type DrainResult =
    { replayedEntries: int
      remainingEntries: int64 }

  type ArchiveOldResult =
    { previousMarkerStatus: string option
      archivePath: string
      replacedExistingArchive: bool }

  type ResetMigrationResult =
    { previousOldMarkerStatus: string option
      oldMarkerDropped: bool
      oldLogDropped: bool
      previousNewStatus: string option
      newDatabaseExisted: bool
      newDatabaseDeleted: bool }

  type ResetMigrationPlan =
    { previousOldMarkerStatus: string option
      oldMarkerPresent: bool
      oldLogPresent: bool
      previousNewStatus: string option
      newDatabaseExisted: bool
      willDropOldMarker: bool
      willDropOldLog: bool
      willDeleteNewDatabase: bool
      canApplyReset: bool
      blockedReason: string option }
