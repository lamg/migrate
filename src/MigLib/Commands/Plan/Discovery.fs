module internal MigLib.Commands.Plan.Discovery

open MigLib.Commands.Types
open MigLib.Commands.Migrate.Discovery
open MigLib.Commands.Resolution.Types

let resolvePlanInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  resolveMigrationInputs project
