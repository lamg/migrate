namespace Mig

module internal HotMigrationShared =
  let migrationTables =
    set
      [ "_migration_marker"
        "_migration_log"
        "_migration_status"
        "_migration_progress"
        "_id_mapping"
        "_schema_identity" ]

  let readyResetBlockedMessage (newDbPath: string) =
    $"Refusing reset because new database status is ready at '{newDbPath}'. This command is only for failed or aborted migrations."
