module MigLib.Types

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Schema.Types
open MigLib.Db.Transactions

type ResolvedGeneratedSchemaModule =
  { schema: SqlFile
    schemaHash: string
    dbApp: string
    defaultDbInstance: string }

[<RequireQualifiedAccess>]
type MigError =
  | Regular of string
  | Sqlite of SqliteException
  | Other of Exception

type SqlFile = MigLib.Schema.Types.SqlFile

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

type ResolvedProject =
  { targetDbPath: string
    targetSchema: ResolvedGeneratedSchemaModule
    sourceDbPath: string option
    sourceDbSchema: SqlFile option
    archiveDir: string }
