module internal MigLib.Commands.Migrate.Discovery

open System.Threading.Tasks

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

let findOldSchema (reportProgress: ProgReport) (project: MigProject) : Task<Result<SqlFile option, MigError>> =
  failwith "TODO findOldSchema"

let prepareNewDb (reportProgress: ProgReport) (project: MigProject) : Task<Result<string, MigError>> =
  failwith "TODO prepareNewDb"

let resolveMigrationInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  failwith "TODO resolveMigrationInputs"
