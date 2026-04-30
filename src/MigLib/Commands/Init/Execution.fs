module internal MigLib.Commands.Init.Execution

open System.Threading.Tasks

open MigLib.Commands.Types

let runInitWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<InitResult, MigError>> =
  failwith "TODO runInitWithSchema"

let init (project: MigProject) : Task<Result<InitResult, MigError>> = failwith "TODO init"
