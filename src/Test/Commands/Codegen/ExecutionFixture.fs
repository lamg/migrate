module TestCodegenSchema.MigSchema

open MigLib.Commands.Schema.Types

type Marker = class end

let Schema: SqlFile =
  { measureTypes = []
    inserts =
      [ { table = "codegen_fixture"
          columns = [ "id" ]
          values = [ [ Integer 1 ] ] } ]
    views = []
    tables =
      [ { name = "codegen_fixture"
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
