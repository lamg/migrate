module ExampleSchema.MigSchema

open MigLib.Schema.Types

type Marker = class end

let Schema: SqlFile =
  { measureTypes = []
    inserts = []
    views = []
    tables =
      [ { name = "student"
          previousName = None
          dropColumns = []
          columns =
            [ { name = "id"
                previousName = None
                columnType = SqlInteger
                constraints =
                  [ NotNull
                    PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "name"
                previousName = None
                columnType = SqlText
                constraints = [ NotNull; Unique [] ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "age"
                previousName = None
                columnType = SqlInteger
                constraints = [ NotNull; Default(Integer 18) ]
                enumLikeDu = None
                unitOfMeasure = None } ]
          constraints = []
          queryByAnnotations = [ { columns = [ "name" ] } ]
          queryLikeAnnotations = [ { columns = [ "name" ] } ]
          queryByOrCreateAnnotations = [ { columns = [ "name" ] } ]
          selectOneAnnotations = [ SelectOneAnnotation ]
          insertOrIgnoreAnnotations = [ InsertOrIgnoreAnnotation ]
          deleteAllAnnotations = [ DeleteAllAnnotation ]
          upsertAnnotations = [ UpsertAnnotation ] } ]
    indexes = []
    triggers = [] }
