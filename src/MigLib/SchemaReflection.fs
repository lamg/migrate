module MigLib.SchemaReflection

open System
open System.Collections.Generic
open System.Globalization
open System.Reflection
open FsToolkit.ErrorHandling
open Microsoft.FSharp.Reflection
open MigLib.Db
open MigLib.DeclarativeMigrations.Types

type private PrimaryKeyInfo =
  { columnName: string
    sqlType: SqlType
    isAutoIncrement: bool }

let internal toSnakeCase (value: string) : string =
  if String.IsNullOrWhiteSpace value then
    value
  else
    let chars = Text.StringBuilder()

    for i in 0 .. value.Length - 1 do
      let c = value.[i]
      let hasPrev = i > 0
      let hasNext = i < value.Length - 1
      let prev = if hasPrev then value.[i - 1] else '\000'
      let next = if hasNext then value.[i + 1] else '\000'

      if
        hasPrev
        && Char.IsUpper c
        && (Char.IsLower prev || (Char.IsUpper prev && hasNext && Char.IsLower next))
      then
        chars.Append '_' |> ignore

      chars.Append(Char.ToLowerInvariant c) |> ignore

    chars.ToString()

let private toCamelCaseFromSnake (value: string) : string =
  if String.IsNullOrWhiteSpace value then
    value
  else
    let parts = value.Split '_'

    if parts.Length = 0 then
      value
    else
      let head = parts.[0]

      let tail =
        parts
        |> Array.skip 1
        |> Array.map (fun part ->
          if String.IsNullOrWhiteSpace part then
            part
          else
            (string (Char.ToUpperInvariant part.[0])) + part.[1..])

      head + String.Concat tail

let private isRecordType (t: Type) =
  try
    FSharpType.IsRecord(t, true)
  with _ ->
    false

let private isUnionType (t: Type) =
  try
    FSharpType.IsUnion(t, true)
  with _ ->
    false

let private mapPrimitiveSqlType (t: Type) : SqlType option =
  if t = typeof<int64> then Some SqlInteger
  elif t = typeof<string> then Some SqlText
  elif t = typeof<float> then Some SqlReal
  else None

let private getTypeAttributes<'a when 'a :> Attribute> (t: Type) : 'a list =
  t.GetCustomAttributes(typeof<'a>, true) |> Seq.cast<'a> |> Seq.toList

let private resolveFieldByName (fields: PropertyInfo array) (fieldName: string) : Result<PropertyInfo, string> =
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

let private parseDefaultLiteral (value: string) : Expr =
  let trimmed = value.Trim()

  let parsedInt, intValue =
    Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture)

  if parsedInt then
    Integer intValue
  else
    let parsedFloat, floatValue =
      Double.TryParse(trimmed, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.InvariantCulture)

    if parsedFloat then
      Real floatValue
    elif trimmed.StartsWith "'" && trimmed.EndsWith "'" && trimmed.Length >= 2 then
      String(trimmed.[1 .. trimmed.Length - 2])
    else
      Value trimmed

let private buildColumnResolver (fieldColumnPairs: (string * string) list) : Map<string, string> =
  fieldColumnPairs
  |> List.collect (fun (fieldName, columnName) ->
    [ fieldName
      toSnakeCase fieldName
      toCamelCaseFromSnake (toSnakeCase fieldName)
      columnName
      toCamelCaseFromSnake columnName ]
    |> List.map (fun key -> key.ToLowerInvariant(), columnName))
  |> Map.ofList

let private resolveColumnName
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

let private addColumnConstraint
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

let private readPrimaryKeyInfo (recordType: Type) : Result<PrimaryKeyInfo option, string> =
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
            Some
              { columnName = toSnakeCase field.Name
                sqlType = SqlInteger
                isAutoIncrement = true }
        | Some _ -> return! Error $"AutoIncPK on type '{recordType.Name}' must target an int64 field"
        | None -> return! Error $"AutoIncPK on type '{recordType.Name}' must target a primitive int64 field"
      }
    | [], [ pk ] ->
      result {
        let! field = resolveFieldByName fields pk.Column

        match mapPrimitiveSqlType field.PropertyType with
        | Some sqlType ->
          return
            Some
              { columnName = toSnakeCase field.Name
                sqlType = sqlType
                isAutoIncrement = false }
        | None -> return! Error $"PK on type '{recordType.Name}' must target a primitive field (int64, string, float)"
      }
    | [], [] -> Ok None
    | _ -> Error $"Type '{recordType.Name}' defines multiple primary-key attributes"

let private applyConstraintAttributes
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

let private readOnDeleteActions
  (recordType: Type)
  (resolver: Map<string, string>)
  : Result<Map<string, FkAction>, string> =
  result {
    let cascadeAttributes = getTypeAttributes<OnDeleteCascadeAttribute> recordType
    let setNullAttributes = getTypeAttributes<OnDeleteSetNullAttribute> recordType

    let! cascades =
      cascadeAttributes
      |> foldResults
        (fun actions attr ->
          result {
            let! resolved = resolveColumnName resolver recordType.Name attr.Column
            return actions @ [ resolved, Cascade ]
          })
        []

    let! setNulls =
      setNullAttributes
      |> foldResults
        (fun actions attr ->
          result {
            let! resolved = resolveColumnName resolver recordType.Name attr.Column
            return actions @ [ resolved, SetNull ]
          })
        []

    let allActions = cascades @ setNulls

    let duplicates =
      allActions |> List.groupBy fst |> List.filter (fun (_, xs) -> xs.Length > 1)

    if duplicates.IsEmpty then
      return allActions |> Map.ofList
    else
      let columns = duplicates |> List.map fst |> String.concat ", "
      return! Error $"Type '{recordType.Name}' has conflicting on-delete actions for columns: {columns}"
  }

let private readIndexDefinitions
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

let private readQueryAnnotations
  (recordType: Type)
  (resolver: Map<string, string>)
  : Result<
      QueryByAnnotation list * QueryLikeAnnotation list * QueryByOrCreateAnnotation list * InsertOrIgnoreAnnotation list,
      string
     >
  =
  result {
    let selectByAttributes = getTypeAttributes<SelectByAttribute> recordType
    let selectLikeAttributes = getTypeAttributes<SelectLikeAttribute> recordType

    let selectByOrInsertAttributes =
      getTypeAttributes<SelectByOrInsertAttribute> recordType

    let insertOrIgnoreAttributes = getTypeAttributes<InsertOrIgnoreAttribute> recordType

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

    return queryBy, queryLike, queryByOrCreate, insertOrIgnore
  }

let private buildTable
  (schemaTypes: HashSet<Type>)
  (pkByType: Dictionary<Type, PrimaryKeyInfo>)
  (recordType: Type)
  : Result<CreateTable * CreateIndex list, string> =
  result {
    let tableName = toSnakeCase recordType.Name
    let fields = FSharpType.GetRecordFields(recordType, true)

    let! fieldColumnPairs =
      fields
      |> Array.toList
      |> foldResults
        (fun pairs field ->
          match mapPrimitiveSqlType field.PropertyType with
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
          match mapPrimitiveSqlType field.PropertyType with
          | Some sqlType ->
            Ok(
              cols
              @ [ { name = toSnakeCase field.Name
                    columnType = sqlType
                    constraints = [ NotNull ] } ]
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
                      columnType = referencedPk.sqlType
                      constraints = [ NotNull; ForeignKey foreignKey ] } ]
              )
          | None -> Error $"Field '{recordType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
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

    let! queryByAnnotations, queryLikeAnnotations, queryByOrCreateAnnotations, insertOrIgnoreAnnotations =
      readQueryAnnotations recordType resolver

    return
      { name = tableName
        columns = constrainedColumns
        constraints = tableConstraints
        queryByAnnotations = queryByAnnotations
        queryLikeAnnotations = queryLikeAnnotations
        queryByOrCreateAnnotations = queryByOrCreateAnnotations
        insertOrIgnoreAnnotations = insertOrIgnoreAnnotations },
      indexes
  }

type private ViewJoin =
  { leftTable: string
    rightTable: string }

let private chooseViewBaseTable (joins: ViewJoin list) : string =
  let tableOrder =
    joins
    |> List.collect (fun join -> [ join.leftTable; join.rightTable ])
    |> List.distinct

  let tableFrequencies =
    joins
    |> List.collect (fun join -> [ join.leftTable; join.rightTable ])
    |> List.countBy id
    |> Map.ofList

  let rankByTable =
    tableOrder |> List.mapi (fun index tableName -> tableName, index) |> Map.ofList

  tableOrder
  |> List.maxBy (fun tableName -> tableFrequencies[tableName], -rankByTable[tableName])

let private aliasSeed (tableName: string) =
  let segments =
    tableName.Split '_' |> Array.filter (String.IsNullOrWhiteSpace >> not)

  if segments.Length = 0 then
    "t"
  else
    segments
    |> Array.map (fun segment -> Char.ToLowerInvariant segment.[0])
    |> fun chars -> System.String chars

let private tryGetPrimaryKeyColumn (table: CreateTable) : ColumnDef option =
  table.columns
  |> List.tryFind (fun column ->
    column.constraints
    |> List.exists (function
      | PrimaryKey _ -> true
      | _ -> false))

let private getReferencedColumnName (referencedTable: CreateTable) (foreignKey: ForeignKey) =
  match foreignKey.refColumns with
  | head :: _ -> head
  | [] ->
    match tryGetPrimaryKeyColumn referencedTable with
    | Some column -> column.name
    | None -> "id"

let private getForeignKeyReferences (table: CreateTable) (refTableName: string) (refTable: CreateTable) =
  table.columns
  |> List.collect (fun column ->
    column.constraints
    |> List.choose (function
      | ForeignKey fk when String.Equals(fk.refTable, refTableName, StringComparison.OrdinalIgnoreCase) ->
        let referencedColumn = getReferencedColumnName refTable fk
        Some(column.name, referencedColumn)
      | _ -> None))

let private inferJoinCondition
  (tablesByName: Map<string, CreateTable>)
  (leftTableName: string)
  (leftAlias: string)
  (rightTableName: string)
  (rightAlias: string)
  : Result<string, string> =
  result {
    let! leftTable =
      match tablesByName.TryFind leftTableName with
      | Some table -> Ok table
      | None -> Error $"""Join references unknown table "{leftTableName}"."""

    let! rightTable =
      match tablesByName.TryFind rightTableName with
      | Some table -> Ok table
      | None -> Error $"""Join references unknown table "{rightTableName}"."""

    let leftToRight = getForeignKeyReferences leftTable rightTableName rightTable

    match leftToRight with
    | [ (leftColumn, rightColumn) ] -> return $"{leftAlias}.{leftColumn} = {rightAlias}.{rightColumn}"
    | _ :: _ :: _ ->
      return!
        Error
          $"""Join between "{leftTableName}" and "{rightTableName}" is ambiguous (multiple foreign keys from left to right)."""
    | [] ->
      let rightToLeft = getForeignKeyReferences rightTable leftTableName leftTable

      match rightToLeft with
      | [ (rightColumn, leftColumn) ] -> return $"{rightAlias}.{rightColumn} = {leftAlias}.{leftColumn}"
      | _ :: _ :: _ ->
        return!
          Error
            $"""Join between "{leftTableName}" and "{rightTableName}" is ambiguous (multiple foreign keys from right to left)."""
      | [] ->
        return!
          Error
            $"""Join between "{leftTableName}" and "{rightTableName}" has no foreign-key relationship in either direction."""
  }

let private getViewJoinAttributes
  (viewType: Type)
  (typeToTableName: Dictionary<Type, string>)
  : Result<ViewJoin list, string> =
  let attributeData = viewType.GetCustomAttributesData() |> Seq.toList

  attributeData
  |> List.filter (fun attribute -> attribute.AttributeType = typeof<JoinAttribute>)
  |> foldResults
    (fun joins attribute ->
      result {
        if attribute.ConstructorArguments.Count <> 2 then
          return! Error $"""Invalid join attribute on view "{viewType.Name}"."""

        let! leftType =
          match attribute.ConstructorArguments.[0].Value with
          | :? Type as t -> Ok t
          | _ -> Error $"""Invalid left type in join attribute on view "{viewType.Name}"."""

        let! rightType =
          match attribute.ConstructorArguments.[1].Value with
          | :? Type as t -> Ok t
          | _ -> Error $"""Invalid right type in join attribute on view "{viewType.Name}"."""

        let! leftTable =
          match typeToTableName.TryGetValue leftType with
          | true, tableName -> Ok tableName
          | false, _ ->
            Error
              $"""Join on view "{viewType.Name}" references type "{leftType.Name}" that is not part of the reflected schema."""

        let! rightTable =
          match typeToTableName.TryGetValue rightType with
          | true, tableName -> Ok tableName
          | false, _ ->
            Error
              $"""Join on view "{viewType.Name}" references type "{rightType.Name}" that is not part of the reflected schema."""

        return
          joins
          @ [ { leftTable = leftTable
                rightTable = rightTable } ]
      })
    []

let private resolveViewFieldProjection
  (tablesByName: Map<string, CreateTable>)
  (joinedTables: string list)
  (aliasesByTable: Dictionary<string, string>)
  (fieldName: string)
  : Result<string, string> =
  result {
    let fieldSnake = toSnakeCase fieldName

    let prefixedCandidates =
      joinedTables
      |> List.collect (fun tableName ->
        let table = tablesByName[tableName]

        let shortTableName =
          let parts = tableName.Split '_'
          parts.[parts.Length - 1]

        let prefixes =
          [ toCamelCaseFromSnake tableName; toCamelCaseFromSnake shortTableName ]
          |> List.distinct

        prefixes
        |> List.choose (fun prefix ->
          if
            fieldName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && fieldName.Length > prefix.Length
          then
            let remainder = fieldName.Substring prefix.Length

            if String.IsNullOrWhiteSpace remainder then
              None
            else
              let normalizedRemainder =
                (string (Char.ToLowerInvariant remainder.[0]))
                + if remainder.Length > 1 then remainder.[1..] else ""

              let candidateColumn = toSnakeCase normalizedRemainder

              if table.columns |> List.exists (fun column -> column.name = candidateColumn) then
                Some(tableName, candidateColumn)
              else
                None
          else
            None))
      |> List.distinct

    let directCandidates =
      joinedTables
      |> List.choose (fun tableName ->
        let table = tablesByName[tableName]

        if table.columns |> List.exists (fun column -> column.name = fieldSnake) then
          Some(tableName, fieldSnake)
        else
          None)

    let! tableName, columnName =
      match prefixedCandidates with
      | [ candidate ] -> Ok candidate
      | _ :: _ ->
        let candidates =
          prefixedCandidates
          |> List.map (fun (table, column) -> $"{table}.{column}")
          |> String.concat ", "

        Error $"""View field "{fieldName}" is ambiguous. Matching columns: {candidates}."""
      | [] ->
        match directCandidates with
        | [ candidate ] -> Ok candidate
        | _ :: _ ->
          let candidates =
            directCandidates
            |> List.map (fun (table, column) -> $"{table}.{column}")
            |> String.concat ", "

          Error $"""View field "{fieldName}" is ambiguous. Matching columns: {candidates}."""
        | [] ->
          let available =
            joinedTables
            |> List.collect (fun tableName ->
              tablesByName[tableName].columns
              |> List.map (fun column -> $"{tableName}.{column.name}"))
            |> String.concat ", "

          Error
            $"""Unable to resolve view field "{fieldName}" to a joined table column. Available columns: {available}."""

    let alias = aliasesByTable[tableName]
    return $"{alias}.{columnName} AS {fieldSnake}"
  }

let private synthesizeViewSql
  (viewName: string)
  (viewType: Type)
  (joins: ViewJoin list)
  (tablesByName: Map<string, CreateTable>)
  : Result<string * string list, string> =
  result {
    if joins.IsEmpty then
      return! Error $"""View "{viewName}" must declare at least one Join."""

    let baseTable = chooseViewBaseTable joins

    let aliasesByTable = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let usedAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let joinedSet = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let joinedOrder = ResizeArray<string>()

    let getOrCreateAlias (tableName: string) =
      match aliasesByTable.TryGetValue tableName with
      | true, alias -> alias
      | false, _ ->
        let seed = aliasSeed tableName
        let mutable candidate = seed
        let mutable suffix = 2

        while usedAliases.Contains candidate do
          candidate <- $"{seed}{suffix}"
          suffix <- suffix + 1

        usedAliases.Add candidate |> ignore
        aliasesByTable[tableName] <- candidate
        candidate

    let addJoinedTable (tableName: string) =
      joinedSet.Add tableName |> ignore

      if not (joinedOrder.Contains tableName) then
        joinedOrder.Add tableName

    let baseAlias = getOrCreateAlias baseTable
    addJoinedTable baseTable

    let joinClauses = ResizeArray<string>()
    let mutable pendingJoins = joins

    while not pendingJoins.IsEmpty do
      let mutable selected: (int * ViewJoin * string * string) option = None

      for index in 0 .. pendingJoins.Length - 1 do
        if selected.IsNone then
          let join = pendingJoins[index]
          let leftJoined = joinedSet.Contains join.leftTable
          let rightJoined = joinedSet.Contains join.rightTable

          let resolvedDirection =
            match leftJoined, rightJoined with
            | true, false -> Some(join.leftTable, join.rightTable)
            | false, true -> Some(join.rightTable, join.leftTable)
            | _ -> None

          match resolvedDirection with
          | Some(existingTable, newTable) -> selected <- Some(index, join, existingTable, newTable)
          | None -> ()

      match selected with
      | Some(index, join, existingTable, newTable) ->
        let existingAlias = getOrCreateAlias existingTable
        let newAlias = getOrCreateAlias newTable
        let! condition = inferJoinCondition tablesByName existingTable existingAlias newTable newAlias

        joinClauses.Add $"JOIN {newTable} {newAlias} ON {condition}"
        addJoinedTable newTable
        pendingJoins <- pendingJoins |> List.removeAt index
      | None ->
        let duplicateJoin =
          pendingJoins
          |> List.tryFind (fun join -> joinedSet.Contains join.leftTable && joinedSet.Contains join.rightTable)

        match duplicateJoin with
        | Some join ->
          return!
            Error
              $"""Table "{join.rightTable}" is joined more than once in view "{viewName}" (join "{join.leftTable}" -> "{join.rightTable}")."""
        | None ->
          let next = pendingJoins.Head
          return! Error $"""Join chain is disconnected at "{next.leftTable}" -> "{next.rightTable}"."""

    let fields = FSharpType.GetRecordFields(viewType, true) |> Array.toList

    let! selectColumns =
      fields
      |> foldResults
        (fun columns field ->
          result {
            let! projection =
              resolveViewFieldProjection tablesByName (joinedOrder |> Seq.toList) aliasesByTable field.Name

            return columns @ [ projection ]
          })
        []

    let selectClause = String.concat ", " selectColumns

    let sql =
      [ $"CREATE VIEW {viewName} AS"
        $"SELECT {selectClause}"
        $"FROM {baseTable} {baseAlias}" ]
      @ (joinClauses |> Seq.toList)
      |> String.concat "\n"
      |> fun value -> value + ";"

    return sql, joinedOrder |> Seq.toList
  }

let private buildView
  (typeToTableName: Dictionary<Type, string>)
  (tablesByName: Map<string, CreateTable>)
  (viewType: Type)
  : Result<CreateView, string> =
  result {
    let tableName = toSnakeCase viewType.Name
    let fields = FSharpType.GetRecordFields(viewType, true)

    let fieldColumnPairs =
      fields
      |> Array.toList
      |> List.map (fun field -> field.Name, toSnakeCase field.Name)

    let resolver = buildColumnResolver fieldColumnPairs

    let viewSqlAttributes = getTypeAttributes<ViewSqlAttribute> viewType
    let viewAttributes = getTypeAttributes<ViewAttribute> viewType
    let! joins = getViewJoinAttributes viewType typeToTableName

    let! sql, dependencies =
      match viewSqlAttributes with
      | [ single ] ->
        let dependencyNames =
          joins
          |> List.collect (fun join -> [ join.leftTable; join.rightTable ])
          |> List.distinct

        Ok(single.Sql, dependencyNames)
      | [] when not viewAttributes.IsEmpty -> synthesizeViewSql tableName viewType joins tablesByName
      | [] -> Error $"""View type "{viewType.Name}" must define [<ViewSql>] or [<View>] with Join attributes."""
      | _ -> Error $"""View type "{viewType.Name}" defines multiple [<ViewSql>] attributes."""

    let! queryByAnnotations, queryLikeAnnotations, queryByOrCreateAnnotations, insertOrIgnoreAnnotations =
      readQueryAnnotations viewType resolver

    return
      { name = tableName
        sqlTokens = [ sql ]
        dependencies = dependencies
        queryByAnnotations = queryByAnnotations
        queryLikeAnnotations = queryLikeAnnotations
        queryByOrCreateAnnotations = queryByOrCreateAnnotations
        insertOrIgnoreAnnotations = insertOrIgnoreAnnotations }
  }

let private buildUnionExtensionTables
  (schemaTypes: HashSet<Type>)
  (pkByType: Dictionary<Type, PrimaryKeyInfo>)
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
            let fkColumnName = $"{baseTableName}_id"

            let fkColumn =
              { name = fkColumnName
                columnType = referencedPk.sqlType
                constraints =
                  [ NotNull
                    PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = false }
                    ForeignKey
                      { columns = []
                        refTable = baseTableName
                        refColumns = [ referencedPk.columnName ]
                        onDelete = None
                        onUpdate = None } ] }

            let! extensionColumns =
              caseFields
              |> Array.toList
              |> List.skip 1
              |> foldResults
                (fun (cols: ColumnDef list) field ->
                  match mapPrimitiveSqlType field.PropertyType with
                  | Some sqlType ->
                    let fieldName =
                      if field.Name.StartsWith "Item" then
                        $"value_{cols.Length + 1}"
                      else
                        field.Name

                    Ok(
                      cols
                      @ [ { name = toSnakeCase fieldName
                            columnType = sqlType
                            constraints = [ NotNull ] } ]
                    )
                  | None ->
                    Error
                      $"Union case '{unionType.Name}.{unionCase.Name}' has unsupported field type '{field.PropertyType.Name}'. Extension fields must be primitive.")
                []

            let extensionTable =
              { name = extensionTableName
                columns = fkColumn :: extensionColumns
                constraints = []
                queryByAnnotations = []
                queryLikeAnnotations = []
                queryByOrCreateAnnotations = []
                insertOrIgnoreAnnotations = [] }

            return tables @ [ extensionTable ]
          })
    []

let private ensureUniqueTableNames (tables: CreateTable list) : Result<unit, string> =
  let duplicates =
    tables
    |> List.groupBy _.name
    |> List.filter (fun (_, grouped) -> grouped.Length > 1)

  if duplicates.IsEmpty then
    Ok()
  else
    let names = duplicates |> List.map fst |> String.concat ", "
    Error $"Schema produced duplicate table names: {names}"

let internal buildSchemaFromTypes (types: Type list) : Result<SqlFile, string> =
  if types.IsEmpty then
    Error "No types were provided for schema reflection"
  else
    result {
      let schemaTypes = HashSet<Type>(types)

      let tableRecordTypes =
        types
        |> List.filter isRecordType
        |> List.filter (fun t ->
          getTypeAttributes<ViewAttribute> t |> List.isEmpty
          && getTypeAttributes<ViewSqlAttribute> t |> List.isEmpty)

      let viewTypes =
        types
        |> List.filter isRecordType
        |> List.filter (fun t ->
          getTypeAttributes<ViewAttribute> t |> List.isEmpty |> not
          || getTypeAttributes<ViewSqlAttribute> t |> List.isEmpty |> not)

      let unionTypes = types |> List.filter isUnionType

      let! pkByType =
        tableRecordTypes
        |> foldResults
          (fun pairs recordType ->
            result {
              let! pkInfo = readPrimaryKeyInfo recordType

              match pkInfo with
              | Some pk -> return pairs @ [ recordType, pk ]
              | None -> return pairs
            })
          []
        |> Result.map (fun pairs ->
          let dictionary = Dictionary<Type, PrimaryKeyInfo>()

          for key, value in pairs do
            dictionary[key] <- value

          dictionary)

      let typeToTableName = Dictionary<Type, string>()

      for tableType in tableRecordTypes do
        typeToTableName[tableType] <- toSnakeCase tableType.Name

      let! tableResults =
        tableRecordTypes
        |> foldResults
          (fun results recordType ->
            result {
              let! table = buildTable schemaTypes pkByType recordType
              return results @ [ table ]
            })
          []

      let reflectedTables = tableResults |> List.map fst

      let reflectedIndexes =
        tableResults
        |> List.collect snd
        |> List.distinctBy (fun index -> index.name, index.table, index.columns)

      let! extensionTables =
        unionTypes
        |> foldResults
          (fun allTables unionType ->
            result {
              let! extensionSet = buildUnionExtensionTables schemaTypes pkByType unionType
              return allTables @ extensionSet
            })
          []

      let allTables = reflectedTables @ extensionTables

      do! ensureUniqueTableNames allTables

      let tablesByName =
        allTables |> List.map (fun table -> table.name, table) |> Map.ofList

      let! views =
        viewTypes
        |> foldResults
          (fun xs viewType ->
            result {
              let! view = buildView typeToTableName tablesByName viewType
              return xs @ [ view ]
            })
          []

      return
        { emptyFile with
            tables = allTables
            indexes = reflectedIndexes
            views = views }
    }

let internal buildSchemaFromAssembly (assembly: Assembly) : Result<SqlFile, string> =
  let types =
    assembly.GetTypes()
    |> Array.filter (fun t -> t.Assembly = assembly)
    |> Array.filter (fun t -> isRecordType t || isUnionType t)
    |> Array.toList

  buildSchemaFromTypes types
