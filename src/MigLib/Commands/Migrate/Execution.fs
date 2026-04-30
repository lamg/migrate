module internal MigLib.Commands.Migrate.Execution

open System.Threading.Tasks

open MigLib.Commands.Types

let migrate (reportProgress: ProgReport) (project: MigProject) : Task<Result<MigrateResult, MigError>> =
  failwith "TODO migrate"
