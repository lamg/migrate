module MigLib.Commands.Types

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Commands.Schema.Types
open MigLib.DbTransactions

type MigProject =
  {
    // Path to an F# runtime project following MigLib conventions:
    // - P/P.fsproj: runtime project that references the generated database module
    // - P/MigSchema/MigSchema.fsproj: schema project that references MigLib
    // - P/MigSchema/MigSchema.fs: schema source using types and MigLib attributes
    fsProject: string
    // - Each F# project can have multiple databases, which are differentiated by the dbInstance value
    dbInstance: string
    // Directory containing databases, one for each instance, and optionally an archive directory
    // storing old databases
    dbDir: string }

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
