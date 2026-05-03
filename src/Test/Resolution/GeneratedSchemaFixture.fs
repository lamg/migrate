module TestGenerated.Db

open MigLib.Schema.Types

type Marker = class end

[<Literal>]
let DbApp = "generated-fixture"

[<Literal>]
let DefaultDbInstance = "main"

[<Literal>]
let SchemaHash = "0123456789abcdef"

let SchemaIdentity: SchemaIdentity =
  { schemaHash = SchemaHash
    schemaCommit = Some "generated-fixture-commit" }

let Schema: SqlFile =
  { measureTypes = []
    inserts = []
    views = []
    tables =
      [ { name = "generated_fixture"
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
          selectOneAnnotations = []
          insertOrIgnoreAnnotations = []
          deleteAllAnnotations = []
          upsertAnnotations = [] } ]
    indexes = []
    triggers = [] }
