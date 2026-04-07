namespace Mig

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types
open MigLib.Db
open MigLib.Util

open SchemaReflectionNaming

module internal SchemaReflectionAttributes =
  let getTypeAttributes<'a when 'a :> Attribute> (t: Type) : 'a list =
    t.GetCustomAttributes(typeof<'a>, true) |> Seq.cast<'a> |> Seq.toList

  let tryReadPreviousName (memberInfo: MemberInfo) : Result<string option, string> =
    let attributes =
      memberInfo.GetCustomAttributes(typeof<PreviousNameAttribute>, true)
      |> Seq.cast<PreviousNameAttribute>
      |> Seq.toList

    match attributes with
    | [] -> Ok None
    | [ attribute ] when String.IsNullOrWhiteSpace attribute.Name ->
      Error $"PreviousName on '{memberInfo.Name}' cannot be empty."
    | [ attribute ] -> Ok(Some attribute.Name)
    | _ -> Error $"Member '{memberInfo.Name}' defines multiple PreviousName attributes."

  let readDropColumns (recordType: Type) : Result<string list, string> =
    let dropColumns =
      getTypeAttributes<DropColumnAttribute> recordType |> List.map _.Name

    let emptyNames = dropColumns |> List.filter String.IsNullOrWhiteSpace

    if not emptyNames.IsEmpty then
      Error $"Type '{recordType.Name}' declares DropColumn with an empty name."
    else
      let duplicateNames =
        dropColumns
        |> List.groupBy (fun name -> name.ToLowerInvariant())
        |> List.filter (fun (_, entries) -> entries.Length > 1)
        |> List.map (fun (_, entries) -> entries.Head)
        |> List.sort

      if duplicateNames.IsEmpty then
        Ok dropColumns
      else
        let duplicates = String.concat ", " duplicateNames
        Error $"Type '{recordType.Name}' declares duplicate DropColumn values: {duplicates}"

  let resolveFieldByName (fields: PropertyInfo array) (fieldName: string) : Result<PropertyInfo, string> =
    let target = fieldName.ToLowerInvariant()

    let matches =
      fields
      |> Array.filter (fun field ->
        let byName = field.Name.ToLowerInvariant() = target
        let bySnake = toSnakeCase field.Name |> fun x -> x.ToLowerInvariant() = target
        byName || bySnake)

    match matches with
    | [| field |] -> Ok field
    | [||] ->
      let available = fields |> Array.map _.Name |> String.concat ", "
      Error $"Field '{fieldName}' not found. Available fields: {available}"
    | _ ->
      let candidateNames = matches |> Array.map _.Name |> String.concat ", "
      Error $"Field '{fieldName}' is ambiguous. Candidates: {candidateNames}"

  let buildColumnResolver (fieldColumnPairs: (string * string) list) : Map<string, string> =
    fieldColumnPairs
    |> List.collect (fun (fieldName, columnName) ->
      [ fieldName
        toSnakeCase fieldName
        toCamelCaseFromSnake (toSnakeCase fieldName)
        columnName
        toCamelCaseFromSnake columnName ]
      |> List.map (fun key -> key.ToLowerInvariant(), columnName))
    |> Map.ofList

  let buildRelationshipResolver (fieldColumns: (string * string list) list) : Map<string, string list> =
    fieldColumns
    |> List.collect (fun (fieldName, columns) ->
      [ fieldName
        toSnakeCase fieldName
        toCamelCaseFromSnake (toSnakeCase fieldName) ]
      |> List.map (fun key -> key.ToLowerInvariant(), columns))
    |> Map.ofList

  let getForeignKeyColumnNamesForPrefix (prefix: string) (referencedPk: PrimaryKeyInfo list) : string list =
    match referencedPk with
    | [ _ ] -> [ $"{prefix}_id" ]
    | pks -> pks |> List.map (fun pk -> $"{prefix}_{pk.columnName}")

  let getForeignKeyColumnNames (fieldName: string) (referencedPk: PrimaryKeyInfo list) : string list =
    getForeignKeyColumnNamesForPrefix (toSnakeCase fieldName) referencedPk

  let resolveColumnName
    (resolver: Map<string, string>)
    (typeName: string)
    (rawColumnName: string)
    : Result<string, string> =
    let key = rawColumnName.ToLowerInvariant()

    match resolver.TryFind key with
    | Some resolved -> Ok resolved
    | None ->
      let available =
        resolver
        |> Map.toList
        |> List.map fst
        |> List.distinct
        |> List.sort
        |> String.concat ", "

      Error $"Column '{rawColumnName}' was not found in type '{typeName}'. Available names: {available}"

  let addColumnConstraint
    (columnName: string)
    (constraintToAdd: ColumnConstraint)
    (columns: ColumnDef list)
    : Result<ColumnDef list, string> =
    let mutable found = false

    let updated =
      columns
      |> List.map (fun column ->
        if String.Equals(column.name, columnName, StringComparison.OrdinalIgnoreCase) then
          found <- true

          { column with
              constraints = column.constraints @ [ constraintToAdd ] }
        else
          column)

    if found then
      Ok updated
    else
      Error $"Column '{columnName}' was not found while applying a constraint"

  let readPrimaryKeyInfo (recordType: Type) : Result<PrimaryKeyInfo list, string> =
    let fields = FSharpType.GetRecordFields(recordType, true)
    let autoAttributes = getTypeAttributes<AutoIncPKAttribute> recordType
    let pkAttributes = getTypeAttributes<PKAttribute> recordType

    if not autoAttributes.IsEmpty && not pkAttributes.IsEmpty then
      Error $"Type '{recordType.Name}' has both AutoIncPK and PK attributes. Use only one."
    else
      match autoAttributes, pkAttributes with
      | [ auto ], [] ->
        result {
          let! field = resolveFieldByName fields auto.Column

          match mapPrimitiveSqlType field.PropertyType with
          | Some SqlInteger ->
            return
              [ { columnName = toSnakeCase field.Name
                  sqlType = SqlInteger
                  isAutoIncrement = true } ]
          | Some _ -> return! Error $"AutoIncPK on type '{recordType.Name}' must target an int64 field"
          | None -> return! Error $"AutoIncPK on type '{recordType.Name}' must target a primitive int64 field"
        }
      | [], pkAttrs when not pkAttrs.IsEmpty ->
        result {
          let requestedColumns =
            pkAttrs |> List.collect (fun pk -> pk.Columns |> List.ofArray)

          if requestedColumns.IsEmpty then
            return! Error $"Type '{recordType.Name}' defines PK attributes without any columns."

          let duplicateColumns =
            requestedColumns
            |> List.groupBy (fun column -> column.ToLowerInvariant())
            |> List.filter (fun (_, entries) -> entries.Length > 1)
            |> List.map (fun (_, entries) -> entries.Head)

          if not duplicateColumns.IsEmpty then
            let duplicates = duplicateColumns |> String.concat ", "
            return! Error $"Type '{recordType.Name}' defines duplicate PK columns: {duplicates}"

          let! primaryKeyInfo =
            requestedColumns
            |> traverseResultM (fun column ->
              result {
                let! field = resolveFieldByName fields column

                match mapPrimitiveSqlType field.PropertyType with
                | Some sqlType ->
                  return
                    { columnName = toSnakeCase field.Name
                      sqlType = sqlType
                      isAutoIncrement = false }
                | None ->
                  return! Error $"PK on type '{recordType.Name}' must target a primitive field (int64, string, float)"
              })

          let duplicateResolvedColumns =
            primaryKeyInfo
            |> List.groupBy (fun pk -> pk.columnName.ToLowerInvariant())
            |> List.filter (fun (_, entries) -> entries.Length > 1)
            |> List.map (fun (_, entries) -> entries.Head.columnName)

          if not duplicateResolvedColumns.IsEmpty then
            let duplicates = duplicateResolvedColumns |> String.concat ", "
            return! Error $"Type '{recordType.Name}' defines duplicate PK columns: {duplicates}"

          return primaryKeyInfo
        }
      | [], [] -> Ok []
      | _ -> Error $"Type '{recordType.Name}' defines multiple AutoIncPK attributes"

  let applyConstraintAttributes
    (recordType: Type)
    (resolver: Map<string, string>)
    (columns: ColumnDef list)
    : Result<ColumnDef list * ColumnConstraint list, string> =
    result {
      let uniqueAttributes = getTypeAttributes<UniqueAttribute> recordType
      let defaultAttributes = getTypeAttributes<DefaultAttribute> recordType
      let defaultExprAttributes = getTypeAttributes<DefaultExprAttribute> recordType

      let! columnsAfterDefaults =
        defaultAttributes
        |> foldResults
          (fun cols attr ->
            result {
              let! resolved = resolveColumnName resolver recordType.Name attr.Column
              return! addColumnConstraint resolved (Default(parseDefaultLiteral attr.Value)) cols
            })
          columns

      let! columnsAfterDefaultExprs =
        defaultExprAttributes
        |> foldResults
          (fun cols attr ->
            result {
              let! resolved = resolveColumnName resolver recordType.Name attr.Column
              return! addColumnConstraint resolved (Default(Value $"({attr.Expr})")) cols
            })
          columnsAfterDefaults

      let! columnsAfterUnique, tableConstraints =
        uniqueAttributes
        |> foldResults
          (fun (cols, constraints) attr ->
            result {
              let! resolvedColumns =
                attr.Columns
                |> Seq.toList
                |> foldResults
                  (fun names raw ->
                    result {
                      let! resolved = resolveColumnName resolver recordType.Name raw
                      return names @ [ resolved ]
                    })
                  []

              match resolvedColumns with
              | [ singleColumn ] ->
                let! updatedColumns = addColumnConstraint singleColumn (Unique []) cols
                return updatedColumns, constraints
              | many -> return cols, constraints @ [ Unique many ]
            })
          (columnsAfterDefaultExprs, [])

      return columnsAfterUnique, tableConstraints
    }

  let readOnDeleteActions
    (recordType: Type)
    (resolver: Map<string, string>)
    (relationshipResolver: Map<string, string list>)
    : Result<Map<string list, FkAction>, string> =
    result {
      let cascadeAttributes = getTypeAttributes<OnDeleteCascadeAttribute> recordType
      let setNullAttributes = getTypeAttributes<OnDeleteSetNullAttribute> recordType

      let resolveOnDeleteTarget (rawTarget: string) : Result<string list, string> =
        let key = rawTarget.ToLowerInvariant()

        match relationshipResolver.TryFind key, resolver.TryFind key with
        | Some columns, _ -> Ok columns
        | None, Some column -> Ok [ column ]
        | None, None ->
          let availableColumns = resolver |> Map.toList |> List.map fst
          let availableRelationships = relationshipResolver |> Map.toList |> List.map fst

          let available =
            availableColumns @ availableRelationships
            |> List.distinct
            |> List.sort
            |> String.concat ", "

          Error $"Column '{rawTarget}' was not found in type '{recordType.Name}'. Available names: {available}"

      let! cascades =
        cascadeAttributes
        |> foldResults
          (fun actions attr ->
            result {
              let! resolved = resolveOnDeleteTarget attr.Column
              return actions @ [ resolved, Cascade ]
            })
          []

      let! setNulls =
        setNullAttributes
        |> foldResults
          (fun actions attr ->
            result {
              let! resolved = resolveOnDeleteTarget attr.Column
              return actions @ [ resolved, SetNull ]
            })
          []

      let allActions = cascades @ setNulls

      let duplicates =
        allActions |> List.groupBy fst |> List.filter (fun (_, xs) -> xs.Length > 1)

      if duplicates.IsEmpty then
        return allActions |> Map.ofList
      else
        let columns =
          duplicates |> List.map (fst >> String.concat ", ") |> String.concat "; "

        return! Error $"Type '{recordType.Name}' has conflicting on-delete actions for columns: {columns}"
    }

  let readIndexDefinitions
    (tableName: string)
    (recordType: Type)
    (resolver: Map<string, string>)
    : Result<CreateIndex list, string> =
    let indexAttributes = getTypeAttributes<IndexAttribute> recordType

    indexAttributes
    |> foldResults
      (fun indexes attr ->
        result {
          let! resolvedColumns =
            attr.Columns
            |> Seq.toList
            |> foldResults
              (fun names raw ->
                result {
                  let! resolved = resolveColumnName resolver recordType.Name raw
                  return names @ [ resolved ]
                })
              []

          let indexKey = String.concat "_" resolvedColumns
          let indexName = $"ix_{tableName}_{indexKey}"

          return
            indexes
            @ [ { name = indexName
                  table = tableName
                  columns = resolvedColumns } ]
        })
      []
    |> Result.map List.distinct

  let readQueryAnnotations
    (recordType: Type)
    (resolver: Map<string, string>)
    : Result<
        QueryByAnnotation list *
        QueryLikeAnnotation list *
        QueryByOrCreateAnnotation list *
        InsertOrIgnoreAnnotation list *
        DeleteAllAnnotation list *
        UpsertAnnotation list,
        string
       >
    =
    result {
      let selectByAttributes = getTypeAttributes<SelectByAttribute> recordType
      let selectLikeAttributes = getTypeAttributes<SelectLikeAttribute> recordType

      let selectByOrInsertAttributes =
        getTypeAttributes<SelectByOrInsertAttribute> recordType

      let insertOrIgnoreAttributes = getTypeAttributes<InsertOrIgnoreAttribute> recordType
      let deleteAllAttributes = getTypeAttributes<DeleteAllAttribute> recordType
      let upsertAttributes = getTypeAttributes<UpsertAttribute> recordType

      let! queryBy =
        selectByAttributes
        |> foldResults
          (fun acc attr ->
            result {
              let! columns =
                attr.Columns
                |> Seq.toList
                |> foldResults
                  (fun names raw ->
                    result {
                      let! resolved = resolveColumnName resolver recordType.Name raw
                      return names @ [ resolved ]
                    })
                  []

              return acc @ [ ({ columns = columns }: QueryByAnnotation) ]
            })
          []

      let! queryLike =
        selectLikeAttributes
        |> foldResults
          (fun acc attr ->
            result {
              let! column = resolveColumnName resolver recordType.Name attr.Column
              return acc @ [ ({ columns = [ column ] }: QueryLikeAnnotation) ]
            })
          []

      let! queryByOrCreate =
        selectByOrInsertAttributes
        |> foldResults
          (fun acc attr ->
            result {
              let! columns =
                attr.Columns
                |> Seq.toList
                |> foldResults
                  (fun names raw ->
                    result {
                      let! resolved = resolveColumnName resolver recordType.Name raw
                      return names @ [ resolved ]
                    })
                  []

              return acc @ [ ({ columns = columns }: QueryByOrCreateAnnotation) ]
            })
          []

      let insertOrIgnore =
        if insertOrIgnoreAttributes.IsEmpty then
          []
        else
          [ InsertOrIgnoreAnnotation ]

      let deleteAll =
        if deleteAllAttributes.IsEmpty then
          []
        else
          [ DeleteAllAnnotation ]

      let upsert =
        if upsertAttributes.IsEmpty then
          []
        else
          [ UpsertAnnotation ]

      return queryBy, queryLike, queryByOrCreate, insertOrIgnore, deleteAll, upsert
    }
