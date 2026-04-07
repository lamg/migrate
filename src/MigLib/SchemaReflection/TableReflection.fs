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
    (pkByType: Dictionary<Type, PrimaryKeyInfo list>)
    (recordType: Type)
    : Result<CreateTable * CreateIndex list, string> =
    result {
      let tableName = toSnakeCase recordType.Name
      let! previousTableName = tryReadPreviousName recordType
      let fields = FSharpType.GetRecordFields(recordType, true)

      let! fieldColumnPairs, relationshipColumnPairs =
        fields
        |> Array.toList
        |> foldResults
          (fun (pairs, relationships) field ->
            match mapSupportedScalarType field.PropertyType with
            | Some _ -> Ok(pairs @ [ field.Name, toSnakeCase field.Name ], relationships)
            | None when isRecordType field.PropertyType ->
              if not (schemaTypes.Contains field.PropertyType) then
                Error $"Field '{recordType.Name}.{field.Name}' references '{field.PropertyType.Name}' outside schema"
              else
                match pkByType.TryGetValue field.PropertyType with
                | false, _ ->
                  Error
                    $"Field '{recordType.Name}.{field.Name}' references '{field.PropertyType.Name}' which does not declare PK or AutoIncPK"
                | true, referencedPk ->
                  let fkColumnNames = getForeignKeyColumnNames field.Name referencedPk

                  let columnPairs =
                    fkColumnNames |> List.map (fun columnName -> columnName, columnName)

                  Ok(pairs @ columnPairs, relationships @ [ field.Name, fkColumnNames ])
            | None -> Error $"Field '{recordType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
          ([], [])

      let resolver = buildColumnResolver fieldColumnPairs
      let relationshipResolver = buildRelationshipResolver relationshipColumnPairs
      let! onDeleteByColumns = readOnDeleteActions recordType resolver relationshipResolver

      let! baseColumns, relationshipConstraints =
        fields
        |> Array.toList
        |> foldResults
          (fun (cols, constraints) field ->
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
                        unitOfMeasure = None } ],
                  constraints
                )
              | None when isRecordType field.PropertyType ->
                match pkByType.TryGetValue field.PropertyType with
                | false, _ ->
                  Error
                    $"Field '{recordType.Name}.{field.Name}' references '{field.PropertyType.Name}' which does not declare PK or AutoIncPK"
                | true, referencedPk ->
                  let fkColumnNames = getForeignKeyColumnNames field.Name referencedPk
                  let onDelete = onDeleteByColumns.TryFind fkColumnNames

                  if previousColumnName.IsSome && referencedPk.Length > 1 then
                    Error
                      $"Field '{recordType.Name}.{field.Name}' expands to multiple foreign-key columns and cannot also declare PreviousName."
                  else
                    let fkColumns =
                      (fkColumnNames, referencedPk)
                      ||> List.map2 (fun fkColumnName referencedPk ->
                        { name = fkColumnName
                          previousName = previousColumnName
                          columnType = referencedPk.sqlType
                          constraints = [ NotNull ]
                          enumLikeDu = None
                          unitOfMeasure = None })

                    match fkColumns, referencedPk with
                    | [ fkColumn ], [ referencedPkColumn ] ->
                      let foreignKey =
                        { columns = []
                          refTable = toSnakeCase field.PropertyType.Name
                          refColumns = [ referencedPkColumn.columnName ]
                          onDelete = onDelete
                          onUpdate = None }

                      let fkColumnWithConstraint =
                        { fkColumn with
                            constraints = fkColumn.constraints @ [ ForeignKey foreignKey ] }

                      Ok(cols @ [ fkColumnWithConstraint ], constraints)
                    | _ ->
                      let foreignKey =
                        { columns = fkColumnNames
                          refTable = toSnakeCase field.PropertyType.Name
                          refColumns = referencedPk |> List.map _.columnName
                          onDelete = onDelete
                          onUpdate = None }

                      Ok(cols @ fkColumns, constraints @ [ ForeignKey foreignKey ])
              | None ->
                Error $"Field '{recordType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
          ([], [])

      let! primaryKeyInfo = readPrimaryKeyInfo recordType

      let! columnsWithPrimaryKey, primaryKeyConstraints =
        match primaryKeyInfo with
        | [] -> Ok(baseColumns, [])
        | [ pkInfo ] when pkInfo.isAutoIncrement ->
          result {
            let! columnsWithPrimaryKey =
              addColumnConstraint
                pkInfo.columnName
                (PrimaryKey
                  { constraintName = None
                    columns = []
                    isAutoincrement = true })
                baseColumns

            return columnsWithPrimaryKey, []
          }
        | pks ->
          Ok(
            baseColumns,
            [ PrimaryKey
                { constraintName = None
                  columns = pks |> List.map _.columnName
                  isAutoincrement = false } ]
          )

      let! constrainedColumns, attributeTableConstraints =
        applyConstraintAttributes recordType resolver columnsWithPrimaryKey

      let! explicitForeignKeyConstraints =
        readForeignKeyAttributes recordType resolver onDeleteByColumns
        |> Result.map (List.map ForeignKey)

      let! indexes = readIndexDefinitions tableName recordType resolver
      let! dropColumns = readDropColumns recordType

      let! queryByAnnotations,
           queryLikeAnnotations,
           queryByOrCreateAnnotations,
           insertOrIgnoreAnnotations,
           deleteAllAnnotations,
           upsertAnnotations = readQueryAnnotations recordType resolver

      return
        { name = tableName
          previousName = previousTableName
          dropColumns = dropColumns
          columns = constrainedColumns
          constraints =
            primaryKeyConstraints
            @ relationshipConstraints
            @ attributeTableConstraints
            @ explicitForeignKeyConstraints
          queryByAnnotations = queryByAnnotations
          queryLikeAnnotations = queryLikeAnnotations
          queryByOrCreateAnnotations = queryByOrCreateAnnotations
          insertOrIgnoreAnnotations = insertOrIgnoreAnnotations
          deleteAllAnnotations = deleteAllAnnotations
          upsertAnnotations = upsertAnnotations },
        indexes
    }
