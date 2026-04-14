namespace Mig

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types
open MigLib.Util

open SchemaReflectionNaming
open SchemaReflectionAttributes

module internal SchemaReflectionUnionExtensions =
  let buildUnionExtensionTables
    (schemaTypes: HashSet<Type>)
    (pkByType: Dictionary<Type, PrimaryKeyInfo list>)
    (unionType: Type)
    : Result<CreateTable list, string> =
    let unionCases = FSharpType.GetUnionCases(unionType, true) |> Array.toList

    unionCases
    |> foldResults
      (fun tables unionCase ->
        let caseFields = unionCase.GetFields()

        if caseFields.Length < 2 then
          Ok tables
        else
          let baseType = caseFields.[0].PropertyType

          if not (isRecordType baseType) then
            Ok tables
          elif not (schemaTypes.Contains baseType) then
            Ok tables
          else
            result {
              let! referencedPk =
                match pkByType.TryGetValue baseType with
                | true, pk -> Ok pk
                | false, _ ->
                  Error
                    $"Union case '{unionType.Name}.{unionCase.Name}' references base type '{baseType.Name}' without PK"

              let baseTableName = toSnakeCase baseType.Name

              let aspectRaw =
                if unionCase.Name.StartsWith "With" && unionCase.Name.Length > 4 then
                  unionCase.Name.[4..]
                else
                  unionCase.Name

              let extensionTableName = $"{baseTableName}_{toSnakeCase aspectRaw}"
              let fkColumnNames = getForeignKeyColumnNamesForPrefix baseTableName referencedPk

              let fkColumns =
                (fkColumnNames, referencedPk)
                ||> List.map2 (fun fkColumnName referencedPk ->
                  { name = fkColumnName
                    previousName = None
                    columnType = referencedPk.sqlType
                    constraints = [ NotNull ]
                    enumLikeDu = None
                    unitOfMeasure = None })

              let tableConstraints =
                match fkColumns, referencedPk with
                | [ fkColumn ], [ referencedPkColumn ] ->
                  [ ForeignKey
                      { columns = []
                        refTable = baseTableName
                        refColumns = [ referencedPkColumn.columnName ]
                        onDelete = None
                        onUpdate = None }
                    PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = false } ]
                  |> fun constraints ->
                    [ { fkColumn with
                          constraints = fkColumn.constraints @ constraints } ],
                    []
                | _ ->
                  fkColumns,
                  [ PrimaryKey
                      { constraintName = None
                        columns = fkColumnNames
                        isAutoincrement = false }
                    ForeignKey
                      { columns = fkColumnNames
                        refTable = baseTableName
                        refColumns = referencedPk |> List.map _.columnName
                        onDelete = None
                        onUpdate = None } ]

              let fkColumns, extensionConstraints = tableConstraints

              let! extensionColumns =
                caseFields
                |> Array.toList
                |> List.skip 1
                |> foldResults
                  (fun (cols: ColumnDef list) field ->
                    match mapSupportedScalarType field.PropertyType with
                    | Some(sqlType, enumLikeDu) ->
                      let fieldName =
                        if field.Name.StartsWith "Item" then
                          $"value_{cols.Length + 1}"
                        else
                          field.Name

                      Ok(
                        cols
                        @ [ { name = toSnakeCase fieldName
                              previousName = None
                              columnType = sqlType
                              constraints = [ NotNull ]
                              enumLikeDu = enumLikeDu
                              unitOfMeasure = None } ]
                      )
                    | None ->
                      Error
                        $"Union case '{unionType.Name}.{unionCase.Name}' has unsupported field type '{field.PropertyType.Name}'. Extension fields must be primitive.")
                  []

              let extensionTable =
                { name = extensionTableName
                  previousName = None
                  dropColumns = []
                  columns = fkColumns @ extensionColumns
                  constraints = extensionConstraints
                  queryByAnnotations = []
                  queryLikeAnnotations = []
                  queryByOrCreateAnnotations = []
                  selectOneAnnotations = []
                  insertOrIgnoreAnnotations = []
                  deleteAllAnnotations = []
                  upsertAnnotations = [] }

              return tables @ [ extensionTable ]
            })
      []
