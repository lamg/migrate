module internal migrate.DeclarativeMigrations.Migration

open FsToolkit.ErrorHandling
open migrate.DeclarativeMigrations.Solve
open migrate.DeclarativeMigrations.Types

let migration (dbSchema: SqlFile, expectedSchema: SqlFile) =
  result {
    let dbSorted, dbMissing = sortFile dbSchema
    let expectedSorted, expectedMissing = sortFile expectedSchema

    do!
      match dbMissing, expectedMissing with
      | [], [] -> Ok()
      | _ -> Error(MissingDependencies(dbMissing, expectedMissing))

    let columnMs = columnMigrations dbSchema.tables expectedSchema.tables
    let tableMs = tableMigrationsSql dbSorted expectedSorted
    let viewMs = viewMigrationsSql dbSorted expectedSorted
    let indexMs = indexMigrationsSql dbSorted expectedSorted
    let triggerMs = triggerMigrationSql dbSorted expectedSorted

    let sql = columnMs @ tableMs @ viewMs @ indexMs @ triggerMs
    return sql
  }
