module TestCodegenSchema.MigSchema

open MigLib.Schema.Types

type Marker = class end

let Schema: SqlFile =
  { measureTypes = []
    inserts =
      [ { table = "codegen_fixture"
          columns = [ "name"; "age" ]
          values = [ [ String "seed"; Integer 18 ] ] }
        { table = "person"
          columns = [ "id"; "name" ]
          values = [ [ String "person-1"; String "Pat" ] ] }
        { table = "person_email"
          columns = [ "id"; "email" ]
          values = [ [ String "person-1"; String "pat@example.com" ] ] } ]
    views =
      [ { name = "codegen_fixture_view"
          previousName = None
          sql = "CREATE VIEW codegen_fixture_view AS SELECT id, name FROM codegen_fixture"
          declaredColumns =
            [ { name = "id"
                columnType = SqlInteger
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "name"
                columnType = SqlText
                enumLikeDu = None
                unitOfMeasure = None } ]
          dependencies = [ "codegen_fixture" ]
          queryByAnnotations = [ { columns = [ "name" ] } ]
          queryLikeAnnotations = []
          queryByOrCreateAnnotations = []
          selectOneAnnotations = [ SelectOneAnnotation ]
          insertOrIgnoreAnnotations = []
          deleteAllAnnotations = []
          upsertAnnotations = [] } ]
    tables =
      [ { name = "codegen_fixture"
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
          upsertAnnotations = [ UpsertAnnotation ] }
        { name = "person"
          previousName = None
          dropColumns = []
          columns =
            [ { name = "id"
                previousName = None
                columnType = SqlText
                constraints =
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = false } ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "name"
                previousName = None
                columnType = SqlText
                constraints = [ NotNull ]
                enumLikeDu = None
                unitOfMeasure = None } ]
          constraints = []
          queryByAnnotations = [ { columns = [ "name" ] } ]
          queryLikeAnnotations = []
          queryByOrCreateAnnotations = [ { columns = [ "name" ] } ]
          selectOneAnnotations = [ SelectOneAnnotation ]
          insertOrIgnoreAnnotations = [ InsertOrIgnoreAnnotation ]
          deleteAllAnnotations = [ DeleteAllAnnotation ]
          upsertAnnotations = [] }
        { name = "person_email"
          previousName = None
          dropColumns = []
          columns =
            [ { name = "id"
                previousName = None
                columnType = SqlText
                constraints =
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = false }
                    ForeignKey
                      { columns = [ "id" ]
                        refTable = "person"
                        refColumns = [ "id" ]
                        onDelete = None
                        onUpdate = None } ]
                enumLikeDu = None
                unitOfMeasure = None }
              { name = "email"
                previousName = None
                columnType = SqlText
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
