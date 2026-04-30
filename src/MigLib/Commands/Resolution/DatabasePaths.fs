module internal MigLib.Commands.Resolution.DatabasePaths

open MigLib.Commands.Types
open MigLib.Commands.Resolution.Types

let resolveDatabasePaths (schema: ResolvedGeneratedSchema) : Result<ResolvedDatabasePaths, MigError> =
  failwith "TODO resolveDatabasePaths"

let resolveTargetDbPath (schema: ResolvedGeneratedSchema) : Result<string, MigError> =
  failwith "TODO resolveTargetDbPath"

let resolveSourceDbPath (schema: ResolvedGeneratedSchema) : Result<string option, MigError> =
  failwith "TODO resolveSourceDbPath"
