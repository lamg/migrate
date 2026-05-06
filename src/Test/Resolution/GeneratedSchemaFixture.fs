module TestGenerated.Db

open MigLib.Schema.Types
open MigLib.Types

type Marker = class end

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

let GeneratedSchema: ResolvedGeneratedSchemaModule =
  { schema = Schema
    schemaHash = "0123456789abcdef"
    dbApp = "generated-fixture"
    defaultDbInstance = "main" }
