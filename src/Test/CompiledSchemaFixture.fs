module CompiledSchemaFixture

open Mig.DeclarativeMigrations.Types
open Mig.HotMigration

type Marker = class end

[<Literal>]
let DbFile = "compiled-fixture.sqlite"

[<Literal>]
let SchemaHash = "0123456789abcdef"

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
