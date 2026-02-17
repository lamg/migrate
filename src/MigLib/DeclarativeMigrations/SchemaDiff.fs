module MigLib.DeclarativeMigrations.SchemaDiff

open System
open System.Collections.Generic
open MigLib.DeclarativeMigrations.Types

type internal ColumnValueSource =
  | SourceColumn of columnName: string
  | DefaultExpr of expr: Expr
  | TypeDefault of columnType: SqlType

type internal ColumnMapping =
  { targetColumn: string
    source: ColumnValueSource }

type internal TableCopyMapping =
  { sourceTable: string
    targetTable: string
    columnMappings: ColumnMapping list
    renamedColumns: (string * string) list
    addedTargetColumns: string list
    droppedSourceColumns: string list }

type internal SchemaDiffResult =
  { addedTables: string list
    removedTables: string list
    renamedTables: (string * string) list
    matchedTables: (string * string) list }

type internal SchemaCopyPlan =
  { diff: SchemaDiffResult
    tableMappings: TableCopyMapping list }

type private RenameCandidate =
  { sourceName: string
    targetName: string
    copiedColumns: int
    targetColumns: int
    nameScore: int }

let private hasConstraint predicate (column: ColumnDef) =
  column.constraints |> List.exists predicate

let private isPrimaryKeyColumn (column: ColumnDef) =
  hasConstraint
    (function
    | PrimaryKey _ -> true
    | _ -> false)
    column

let private isForeignKeyColumn (column: ColumnDef) =
  hasConstraint
    (function
    | ForeignKey _ -> true
    | _ -> false)
    column

let private tableSchemaSignature (table: CreateTable) = table.columns, table.constraints

let private splitNameTokens (name: string) =
  name.Split '_'
  |> Array.filter (String.IsNullOrWhiteSpace >> not)
  |> Array.map (fun token -> token.ToLowerInvariant())
  |> Set.ofArray

let private nameSimilarityScore (leftName: string) (rightName: string) =
  let left = leftName.ToLowerInvariant()
  let right = rightName.ToLowerInvariant()
  let leftTokens = splitNameTokens left
  let rightTokens = splitNameTokens right

  let tokenScore = Set.intersect leftTokens rightTokens |> Set.count

  let exactBonus = if left = right then 20 else 0

  let suffixBonus =
    if
      left.EndsWith("_" + right, StringComparison.Ordinal)
      || right.EndsWith("_" + left, StringComparison.Ordinal)
    then
      4
    else
      0

  exactBonus + (tokenScore * 3) + suffixBonus

let private detectRenamedTables (removedTables: CreateTable list) (addedTables: CreateTable list) =
  let removedBySignature =
    removedTables |> List.groupBy tableSchemaSignature |> Map.ofList

  let addedBySignature =
    addedTables |> List.groupBy tableSchemaSignature |> Map.ofList

  let exactRenames =
    addedBySignature
    |> Map.toList
    |> List.choose (fun (signature, addedWithSameSignature) ->
      match Map.tryFind signature removedBySignature with
      | Some removedWithSameSignature when removedWithSameSignature.Length = 1 && addedWithSameSignature.Length = 1 ->
        Some(removedWithSameSignature.Head, addedWithSameSignature.Head)
      | _ -> None)

  let matchedRemovedByExact =
    exactRenames |> List.map (fun (source, _) -> source.name) |> Set.ofList

  let matchedAddedByExact =
    exactRenames |> List.map (fun (_, target) -> target.name) |> Set.ofList

  let remainingRemoved =
    removedTables
    |> List.filter (fun table -> not (matchedRemovedByExact.Contains table.name))

  let remainingAdded =
    addedTables
    |> List.filter (fun table -> not (matchedAddedByExact.Contains table.name))

  let columnCompatibleCount (sourceTable: CreateTable) (targetTable: CreateTable) =
    let sourceByName =
      sourceTable.columns
      |> List.map (fun column -> column.name, column)
      |> Map.ofList

    let usedSourceColumns = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let isCopyCompatible (sourceColumn: ColumnDef) (targetColumn: ColumnDef) =
      sourceColumn.columnType = targetColumn.columnType
      && isPrimaryKeyColumn sourceColumn = isPrimaryKeyColumn targetColumn
      && isForeignKeyColumn sourceColumn = isForeignKeyColumn targetColumn

    let tryUseColumn (columnName: string) =
      if usedSourceColumns.Add columnName then 1 else 0

    targetTable.columns
    |> List.sumBy (fun targetColumn ->
      match Map.tryFind targetColumn.name sourceByName with
      | Some sourceColumn when isCopyCompatible sourceColumn targetColumn -> tryUseColumn sourceColumn.name
      | _ ->
        let renameCandidates =
          sourceTable.columns
          |> List.filter (fun sourceColumn ->
            not (usedSourceColumns.Contains sourceColumn.name)
            && isCopyCompatible sourceColumn targetColumn)

        match renameCandidates with
        | [ sourceColumn ] -> tryUseColumn sourceColumn.name
        | many ->
          many
          |> List.sortByDescending (fun sourceColumn -> nameSimilarityScore sourceColumn.name targetColumn.name)
          |> List.tryHead
          |> Option.map (fun sourceColumn -> tryUseColumn sourceColumn.name)
          |> Option.defaultValue 0)

  let heuristicCandidates =
    remainingRemoved
    |> List.collect (fun sourceTable ->
      remainingAdded
      |> List.choose (fun targetTable ->
        let copiedColumns = columnCompatibleCount sourceTable targetTable
        let targetColumns = targetTable.columns.Length
        let sourceColumns = sourceTable.columns.Length
        let minColumns = min sourceColumns targetColumns
        let nameScore = nameSimilarityScore sourceTable.name targetTable.name
        let hasStrongOverlap = copiedColumns = minColumns && minColumns > 0
        let hasEnoughCoverage = copiedColumns * 2 >= targetColumns

        if
          copiedColumns > 0
          && (hasStrongOverlap || hasEnoughCoverage)
          && (nameScore > 0 || hasStrongOverlap)
        then
          Some
            { sourceName = sourceTable.name
              targetName = targetTable.name
              copiedColumns = copiedColumns
              targetColumns = targetColumns
              nameScore = nameScore }
        else
          None))

  let heuristicRenames =
    let usedSources = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let usedTargets = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    heuristicCandidates
    |> List.sortByDescending (fun candidate ->
      candidate.copiedColumns, candidate.nameScore, (candidate.targetColumns * -1))
    |> List.fold
      (fun renames candidate ->
        if
          usedSources.Contains candidate.sourceName
          || usedTargets.Contains candidate.targetName
        then
          renames
        else
          usedSources.Add candidate.sourceName |> ignore
          usedTargets.Add candidate.targetName |> ignore
          (candidate.sourceName, candidate.targetName) :: renames)
      []
    |> List.rev

  ((exactRenames |> List.map (fun (source, target) -> source.name, target.name))
   @ heuristicRenames)
  |> List.distinct
  |> List.sortBy snd

let internal diffSchemas (source: SqlFile) (target: SqlFile) : SchemaDiffResult =
  let sourceNames = source.tables |> List.map _.name |> Set.ofList
  let targetNames = target.tables |> List.map _.name |> Set.ofList
  let unchangedNames = Set.intersect sourceNames targetNames |> Set.toList

  let removedCandidates =
    source.tables
    |> List.filter (fun table -> unchangedNames |> List.contains table.name |> not)

  let addedCandidates =
    target.tables
    |> List.filter (fun table -> unchangedNames |> List.contains table.name |> not)

  let renamedTables = detectRenamedTables removedCandidates addedCandidates
  let renamedSources = renamedTables |> List.map fst |> Set.ofList
  let renamedTargets = renamedTables |> List.map snd |> Set.ofList

  let removedTables =
    removedCandidates
    |> List.map _.name
    |> List.filter (fun name -> not (renamedSources.Contains name))
    |> List.sort

  let addedTables =
    addedCandidates
    |> List.map _.name
    |> List.filter (fun name -> not (renamedTargets.Contains name))
    |> List.sort

  let matchedTables =
    (unchangedNames |> List.map (fun name -> name, name)) @ renamedTables
    |> List.sortBy snd

  { addedTables = addedTables
    removedTables = removedTables
    renamedTables = renamedTables
    matchedTables = matchedTables }

let private tryFindColumnDefault (column: ColumnDef) =
  column.constraints
  |> List.tryPick (function
    | Default expr -> Some expr
    | _ -> None)

let private isRenameCompatible (sourceColumn: ColumnDef) (targetColumn: ColumnDef) =
  sourceColumn.columnType = targetColumn.columnType
  && isPrimaryKeyColumn sourceColumn = isPrimaryKeyColumn targetColumn
  && isForeignKeyColumn sourceColumn = isForeignKeyColumn targetColumn

let private tryChooseRenameCandidate (targetColumn: ColumnDef) (renameCandidates: ColumnDef list) =
  match renameCandidates with
  | [] -> None
  | [ sourceColumn ] -> Some sourceColumn
  | many ->
    let ranked =
      many
      |> List.map (fun sourceColumn -> sourceColumn, nameSimilarityScore sourceColumn.name targetColumn.name)
      |> List.sortByDescending snd

    match ranked with
    | (best, bestScore) :: (_, secondScore) :: _ when bestScore > secondScore -> Some best
    | _ -> None

let internal buildTableCopyMapping (sourceTable: CreateTable) (targetTable: CreateTable) : TableCopyMapping =
  let sourceColumnsByName =
    sourceTable.columns
    |> List.map (fun column -> column.name, column)
    |> Map.ofList

  let usedSourceColumns = HashSet<string>(StringComparer.OrdinalIgnoreCase)
  let renamedColumns = ResizeArray<string * string>()
  let addedTargetColumns = ResizeArray<string>()

  let resolveColumnSource (targetColumn: ColumnDef) =
    match Map.tryFind targetColumn.name sourceColumnsByName with
    | Some sourceColumn ->
      usedSourceColumns.Add sourceColumn.name |> ignore
      SourceColumn sourceColumn.name
    | None ->
      let renameCandidates =
        sourceTable.columns
        |> List.filter (fun sourceColumn ->
          not (usedSourceColumns.Contains sourceColumn.name)
          && isRenameCompatible sourceColumn targetColumn)

      match tryChooseRenameCandidate targetColumn renameCandidates with
      | Some sourceColumn ->
        let similarityScore = nameSimilarityScore sourceColumn.name targetColumn.name
        let targetDefault = tryFindColumnDefault targetColumn

        match targetDefault with
        | Some defaultExpr when similarityScore = 0 ->
          addedTargetColumns.Add targetColumn.name
          DefaultExpr defaultExpr
        | _ ->
          usedSourceColumns.Add sourceColumn.name |> ignore
          renamedColumns.Add(sourceColumn.name, targetColumn.name)
          SourceColumn sourceColumn.name
      | None ->
        addedTargetColumns.Add targetColumn.name

        match tryFindColumnDefault targetColumn with
        | Some expr -> DefaultExpr expr
        | None -> TypeDefault targetColumn.columnType

  let columnMappings =
    targetTable.columns
    |> List.map (fun targetColumn ->
      { targetColumn = targetColumn.name
        source = resolveColumnSource targetColumn })

  let droppedSourceColumns =
    sourceTable.columns
    |> List.map _.name
    |> List.filter (fun name -> not (usedSourceColumns.Contains name))

  { sourceTable = sourceTable.name
    targetTable = targetTable.name
    columnMappings = columnMappings
    renamedColumns = renamedColumns |> Seq.toList
    addedTargetColumns = addedTargetColumns |> Seq.toList
    droppedSourceColumns = droppedSourceColumns }

let private findTableByName (tables: CreateTable list) (tableName: string) =
  tables
  |> List.find (fun table -> table.name.Equals(tableName, StringComparison.OrdinalIgnoreCase))

let internal buildSchemaCopyPlan (source: SqlFile) (target: SqlFile) : SchemaCopyPlan =
  let diff = diffSchemas source target

  let targetSortRank =
    target.tables |> List.mapi (fun index table -> table.name, index) |> Map.ofList

  let tableMappings =
    diff.matchedTables
    |> List.map (fun (sourceName, targetName) ->
      let sourceTable = findTableByName source.tables sourceName
      let targetTable = findTableByName target.tables targetName
      buildTableCopyMapping sourceTable targetTable)
    |> List.sortBy (fun mapping -> targetSortRank[mapping.targetTable])

  { diff = diff
    tableMappings = tableMappings }

let private escapeSqlString (value: string) = value.Replace("'", "''")

let internal exprToSql (expr: Expr) =
  match expr with
  | String value -> $"'{escapeSqlString value}'"
  | Integer value -> string value
  | Real value -> string value
  | Value value -> value

let internal defaultValueSql (columnType: SqlType) =
  match columnType with
  | SqlInteger -> "0"
  | SqlText -> "''"
  | SqlReal -> "0.0"
  | SqlTimestamp -> "''"
  | SqlString -> "''"
  | SqlFlexible -> "''"

let internal columnSourceSql (source: ColumnValueSource) =
  match source with
  | SourceColumn columnName -> columnName
  | DefaultExpr expr -> exprToSql expr
  | TypeDefault columnType -> defaultValueSql columnType
