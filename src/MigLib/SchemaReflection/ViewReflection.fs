namespace Mig

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types
open MigLib.Db
open MigLib.Util

open SchemaReflectionNaming
open SchemaReflectionAttributes

module internal SchemaReflectionView =
  let chooseViewBaseTable (joins: ViewJoin list) : string =
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

  let aliasSeed (tableName: string) =
    let segments =
      tableName.Split '_' |> Array.filter (String.IsNullOrWhiteSpace >> not)

    if segments.Length = 0 then
      "t"
    else
      segments
      |> Array.map (fun segment -> Char.ToLowerInvariant segment.[0])
      |> fun chars -> System.String chars

  let getPrimaryKeyColumns (table: CreateTable) : string list =
    let tableLevelPk =
      table.constraints
      |> List.tryPick (function
        | PrimaryKey pk when not pk.columns.IsEmpty -> Some pk.columns
        | _ -> None)

    match tableLevelPk with
    | Some columns -> columns
    | None ->
      table.columns
      |> List.filter (fun column ->
        column.constraints
        |> List.exists (function
          | PrimaryKey _ -> true
          | _ -> false))
      |> List.map _.name

  let normalizeReferencedColumns (referencedTable: CreateTable) (foreignKey: ForeignKey) (expectedCount: int) =
    if not foreignKey.refColumns.IsEmpty then
      if foreignKey.refColumns.Length = expectedCount then
        Some foreignKey.refColumns
      else
        None
    else
      let primaryKeyColumns = getPrimaryKeyColumns referencedTable

      if primaryKeyColumns.Length = expectedCount then
        Some primaryKeyColumns
      elif expectedCount = 1 then
        Some [ "id" ]
      else
        None

  let getForeignKeyReferences (table: CreateTable) (refTableName: string) (refTable: CreateTable) =
    let columnForeignKeys =
      table.columns
      |> List.collect (fun column ->
        column.constraints
        |> List.choose (function
          | ForeignKey fk when String.Equals(fk.refTable, refTableName, StringComparison.OrdinalIgnoreCase) ->
            normalizeReferencedColumns refTable fk 1
            |> Option.map (fun referencedColumns -> [ column.name, referencedColumns.Head ])
          | _ -> None))

    let tableForeignKeys =
      table.constraints
      |> List.choose (function
        | ForeignKey fk when
          not fk.columns.IsEmpty
          && String.Equals(fk.refTable, refTableName, StringComparison.OrdinalIgnoreCase)
          ->
          normalizeReferencedColumns refTable fk fk.columns.Length
          |> Option.map (List.zip fk.columns)
        | _ -> None)

    columnForeignKeys @ tableForeignKeys

  let inferJoinCondition
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
      | [ pairs ] ->
        return
          pairs
          |> List.map (fun (leftColumn, rightColumn) -> $"{leftAlias}.{leftColumn} = {rightAlias}.{rightColumn}")
          |> String.concat " AND "
      | _ :: _ :: _ ->
        return!
          Error
            $"""Join between "{leftTableName}" and "{rightTableName}" is ambiguous (multiple foreign keys from left to right)."""
      | [] ->
        let rightToLeft = getForeignKeyReferences rightTable leftTableName leftTable

        match rightToLeft with
        | [ pairs ] ->
          return
            pairs
            |> List.map (fun (rightColumn, leftColumn) -> $"{rightAlias}.{rightColumn} = {leftAlias}.{leftColumn}")
            |> String.concat " AND "
        | _ :: _ :: _ ->
          return!
            Error
              $"""Join between "{leftTableName}" and "{rightTableName}" is ambiguous (multiple foreign keys from right to left)."""
        | [] ->
          return!
            Error
              $"""Join between "{leftTableName}" and "{rightTableName}" has no foreign-key relationship in either direction."""
    }

  let getViewJoinAttributes
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

  let resolveViewFieldProjection
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

  let synthesizeViewSql
    (viewName: string)
    (viewType: Type)
    (joins: ViewJoin list)
    (tablesByName: Map<string, CreateTable>)
    : Result<string * string list, string> =
    result {
      if joins.IsEmpty then
        return! Error $"""View "{viewName}" must declare at least one Join."""

      let baseTable = chooseViewBaseTable joins

      let aliasesByTable = Dictionary<string, string> StringComparer.OrdinalIgnoreCase
      let usedAliases = HashSet<string> StringComparer.OrdinalIgnoreCase
      let joinedSet = HashSet<string> StringComparer.OrdinalIgnoreCase
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

  let buildView
    (typeToTableName: Dictionary<Type, string>)
    (tablesByName: Map<string, CreateTable>)
    (viewType: Type)
    : Result<CreateView, string> =
    result {
      let tableName = toSnakeCase viewType.Name
      let! previousViewName = tryReadPreviousName viewType
      let fields = FSharpType.GetRecordFields(viewType, true)

      let! declaredColumns =
        fields
        |> Array.toList
        |> foldResults
          (fun columns field ->
            match mapSupportedScalarType field.PropertyType with
            | Some(sqlType, enumLikeDu) ->
              Ok(
                columns
                @ [ { name = toSnakeCase field.Name
                      columnType = sqlType
                      enumLikeDu = enumLikeDu
                      unitOfMeasure = None } ]
              )
            | None ->
              Error $"View field '{viewType.Name}.{field.Name}' has unsupported type '{field.PropertyType.Name}'")
          []

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

          Ok($"CREATE VIEW {tableName} AS {single.Sql}", dependencyNames)
        | [] when not viewAttributes.IsEmpty -> synthesizeViewSql tableName viewType joins tablesByName
        | [] -> Error $"""View type "{viewType.Name}" must define [<ViewSql>] or [<View>] with Join attributes."""
        | _ -> Error $"""View type "{viewType.Name}" defines multiple [<ViewSql>] attributes."""

      let! queryByAnnotations,
           queryLikeAnnotations,
           queryByOrCreateAnnotations,
           selectOneAnnotations,
           insertOrIgnoreAnnotations,
           deleteAllAnnotations,
           upsertAnnotations = readQueryAnnotations viewType resolver

      return
        { name = tableName
          previousName = previousViewName
          sql = sql
          declaredColumns = declaredColumns
          dependencies = dependencies
          queryByAnnotations = queryByAnnotations
          queryLikeAnnotations = queryLikeAnnotations
          queryByOrCreateAnnotations = queryByOrCreateAnnotations
          selectOneAnnotations = selectOneAnnotations
          insertOrIgnoreAnnotations = insertOrIgnoreAnnotations
          deleteAllAnnotations = deleteAllAnnotations
          upsertAnnotations = upsertAnnotations }
    }
