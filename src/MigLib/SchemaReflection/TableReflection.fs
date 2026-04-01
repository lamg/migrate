namespace Mig

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types
open MigLib.Util

open SchemaReflectionNaming
open SchemaReflectionAttributes

module internal SchemaReflectionTable =
  let buildTable
    (schemaTypes: HashSet<Type>)
    (pkByType: Dictionary<Type, PrimaryKeyInfo>)
    (recordType: Type)
    : Result<CreateTable * CreateIndex list, string> =
    result {
      let tableName = toSnakeCase recordType.Name
      let! previousTableName = tryReadPreviousName recordType
      let fields = FSharpType.GetRecordFields(recordType, true)

      let! fieldColumnPairs =
        fields
        |> Array.toList
        |> foldResults
          (fun pairs field ->
            match mapSupportedScalarType field.PropertyType with
            | Some _ -> Ok(pairs @ [ field.Name, toSnakeCase field.Name ])
            | None when isRecordType field.PropertyType ->
              if schemaTypes.Contains field.PropertyType then
                Ok(pairs @ [ field.Name, $"{toSnakeCase field.Name}_id" ])
              else
                Error $"Field '{recordType.Name}.{field.Name}' references '{field.PropertyType.Name}' outside schema"
            | None -> Error $"Field '{recordType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
          []

      let resolver = buildColumnResolver fieldColumnPairs
      let! onDeleteByColumn = readOnDeleteActions recordType resolver

      let! baseColumns =
        fields
        |> Array.toList
        |> foldResults
          (fun cols field ->
            let previousColumnNameResult = tryReadPreviousName field

            match previousColumnNameResult with
            | Error error -> Error error
            | Ok previousColumnName ->
              match mapSupportedScalarType field.PropertyType with
              | Some(sqlType, enumLikeDu) ->
                Ok(
                  cols
                  @ [ { name = toSnakeCase field.Name
                        previousName = previousColumnName
                        columnType = sqlType
                        constraints = [ NotNull ]
                        enumLikeDu = enumLikeDu
                        unitOfMeasure = None } ]
                )
              | None when isRecordType field.PropertyType ->
                match pkByType.TryGetValue field.PropertyType with
                | false, _ ->
                  Error
                    $"Field '{recordType.Name}.{field.Name}' references '{field.PropertyType.Name}' which does not declare PK or AutoIncPK"
                | true, referencedPk ->
                  let fkColumnName = $"{toSnakeCase field.Name}_id"

                  let foreignKey =
                    { columns = []
                      refTable = toSnakeCase field.PropertyType.Name
                      refColumns = [ referencedPk.columnName ]
                      onDelete = onDeleteByColumn.TryFind fkColumnName
                      onUpdate = None }

                  Ok(
                    cols
                    @ [ { name = fkColumnName
                          previousName = previousColumnName
                          columnType = referencedPk.sqlType
                          constraints = [ NotNull; ForeignKey foreignKey ]
                          enumLikeDu = None
                          unitOfMeasure = None } ]
                  )
              | None ->
                Error $"Field '{recordType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
          []

      let! primaryKeyInfo = readPrimaryKeyInfo recordType

      let! columnsWithPrimaryKey =
        match primaryKeyInfo with
        | None -> Ok baseColumns
        | Some pkInfo ->
          addColumnConstraint
            pkInfo.columnName
            (PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = pkInfo.isAutoIncrement })
            baseColumns

      let! constrainedColumns, tableConstraints = applyConstraintAttributes recordType resolver columnsWithPrimaryKey
      let! indexes = readIndexDefinitions tableName recordType resolver
      let! dropColumns = readDropColumns recordType

      let! (queryByAnnotations,
            queryLikeAnnotations,
            queryByOrCreateAnnotations,
            insertOrIgnoreAnnotations,
            upsertAnnotations) = readQueryAnnotations recordType resolver

      return
        { name = tableName
          previousName = previousTableName
          dropColumns = dropColumns
          columns = constrainedColumns
          constraints = tableConstraints
          queryByAnnotations = queryByAnnotations
          queryLikeAnnotations = queryLikeAnnotations
          queryByOrCreateAnnotations = queryByOrCreateAnnotations
          insertOrIgnoreAnnotations = insertOrIgnoreAnnotations
          upsertAnnotations = upsertAnnotations },
        indexes
    }
