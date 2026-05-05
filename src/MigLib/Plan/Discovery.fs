module internal MigLib.Plan.Discovery

open System.Threading.Tasks

open MigLib.Types

let resolvePlanInputs (project: ResolvedProject) : Task<Result<ResolvedProject, MigError>> = Task.FromResult(Ok project)
