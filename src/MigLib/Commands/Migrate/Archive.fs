module internal MigLib.Commands.Migrate.Archive

open System.Threading.Tasks

open MigLib.Commands.Types

let markReadonlyAndArchiveOldDb (reportProgress: ProgReport) (oldDbPath: string) : Task<Result<string, MigError>> =
  failwith "TODO markReadonlyAndArchiveOldDb"
