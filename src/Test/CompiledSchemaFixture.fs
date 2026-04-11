module CompiledSchemaFixture

open Mig.DeclarativeMigrations.Types
open Mig.HotMigration
open MigLib.Db
open MigLib.Util

type Marker = class end

[<Literal>]
let DbApp = "compiled-fixture"

[<Literal>]
let DefaultDbInstance = "main"

[<Literal>]
let SchemaHash = "0123456789abcdef"

let DbFileForInstance (instance: string option) =
  buildSchemaBoundDbFileName DbApp instance SchemaHash |> ResultEx.orFail invalidOp

let SchemaIdentity: SchemaIdentity =
  { schemaHash = SchemaHash
    schemaCommit = Some "fixture-commit" }

let Schema: SqlFile =
  { measureTypes = []
    inserts =
      [ { table = "fixture_student"
          columns = [ "id" ]
          values = [ [ Integer 1 ] ] } ]
    views = []
    tables =
      [ { name = "fixture_student"
          previousName = None
          dropColumns = []
          columns =
            [ { name = "id"
                previousName = None
                columnType = SqlInteger
                constraints = [ NotNull ]
                enumLikeDu = None
                unitOfMeasure = None } ]
          constraints = []
          queryByAnnotations = []
          queryLikeAnnotations = []
          queryByOrCreateAnnotations = []
          insertOrIgnoreAnnotations = []
          deleteAllAnnotations = []
          upsertAnnotations = [] } ]
    indexes = []
    triggers = [] }
