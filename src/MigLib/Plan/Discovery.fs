module internal MigLib.Plan.Discovery

open System.Threading.Tasks

open MigLib.Types
open MigLib.Migrate.Discovery

let resolvePlanInputs (project: MigProject) : Task<Result<ResolvedMigProject, MigError>> =
  resolveMigrationInputs project
