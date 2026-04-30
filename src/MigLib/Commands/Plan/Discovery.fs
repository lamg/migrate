module internal MigLib.Commands.Plan.Discovery

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

let resolvePlanInputs (project: MigProject) : Result<ResolvedGeneratedSchema * ResolvedDatabasePaths, MigError> =
  failwith "TODO resolvePlanInputs"
