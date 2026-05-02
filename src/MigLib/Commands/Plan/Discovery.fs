module internal MigLib.Commands.Plan.Discovery

open System.Threading.Tasks

open MigLib.Commands.Types
open MigLib.Commands.Migrate.Discovery

let resolvePlanInputs (project: MigProject) : Task<Result<ResolvedMigProject, MigError>> =
  resolveMigrationInputs project
