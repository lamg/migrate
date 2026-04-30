module internal MigLib.Commands.Resolution.Projects

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

let resolveProject (project: MigProject) : Result<ResolvedProject, MigError> = failwith "TODO resolveProject"

let discoverProject (directory: string) (dbInstance: string) (dbDir: string) : Result<ResolvedProject, MigError> =
  failwith "TODO discoverProject"
