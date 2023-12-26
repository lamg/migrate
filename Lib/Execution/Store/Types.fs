module Migrate.Execution.Store.Types

open Migrate.Types
open Dapper.FSharp.SQLite
open Migrate.DbProject

type StoredMigration =
  { id: int64
    hash: string
    versionRemarks: string
    date: string
    dbFile: string
    schemaVersion: string }

type NewMigration =
  { hash: string
    versionRemarks: string
    date: string
    dbFile: string
    schemaVersion: string }

type Step =
  { migrationId: int64
    stepIndex: int64
    sql: string }

type StepReason =
  { migrationId: int64
    stepIndex: int64
    status: string
    entity: string }

type StoredStepReason =
  { id: int64
    migrationId: int64
    stepIndex: int64
    status: string
    entity: string }

type Error =
  { migrationId: int64
    stepIndex: int64
    error: string }

type SqliteMaster = { sql: string }

type StepLog =
  { reason: Diff
    sql: string
    error: string option }

type MigrationLog =
  { migration: StoredMigration
    steps: StepLog list }

let migrationTable =
  table'<StoredMigration> $"{LoadDbSchema.migrateTablePrefix}migration"

let newMigrationTable =
  table'<NewMigration> $"{LoadDbSchema.migrateTablePrefix}migration"

let stepTable = table'<Step> $"{LoadDbSchema.migrateTablePrefix}step"

let stepReasonTable =
  table'<StepReason> $"{LoadDbSchema.migrateTablePrefix}step_reason"

let storedStepReasonTable =
  table'<StoredStepReason> $"{LoadDbSchema.migrateTablePrefix}step_reason"

let errorTable = table'<Error> $"{LoadDbSchema.migrateTablePrefix}error"
