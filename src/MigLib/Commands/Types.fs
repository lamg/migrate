module MigLib.Commands.Types

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Commands.Schema.Types
open MigLib.DbTransactions

type MigProject =
  { dbInstance: string
    dbDir: string
    targetSchema: SqlFile
    dbApp: string
    schemaIdentity: SchemaIdentity }

type internal ResolvedMigProject =
  { project: MigProject
    targetDbPath: string
    sourceDbPath: string option
    archiveDirectory: string
    archivedDbPaths: string list
    currentDbPath: string option
    sourceSchema: SqlFile option }

[<RequireQualifiedAccess>]
type MigError =
  | Regular of string
  | Sqlite of SqliteException
  | Other of Exception

type SqlFile = MigLib.Commands.Schema.Types.SqlFile

type InitResult =
  { newDbPath: string; seededRows: int64 }

type CodegenResult =
  { outputPath: string
    generatedModuleName: string
    generatedFiles: string list }

type PlanResult =
  { sourceDbPath: string option
    targetDbPath: string
    canMigrate: bool
    supportedDifferences: string list
    unsupportedDifferences: string list }

type MigrateResult =
  { db: DbTxnBuilder
    newDbPath: string
    archivedOldDbPath: string option
    copiedTables: int
    copiedRows: int64 }

type StatusResult =
  { currentDbPath: string option
    archivedDbPaths: string list
    needsMigration: bool }

type ResetResult =
  { restoredDbPath: string option
    removedCurrentDbPath: string option }

type ProgReport = string -> Task<unit>
