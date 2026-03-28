module Mig.DeclarativeMigrations.SchemaDiff

open System
open System.Collections.Generic
open Mig.DeclarativeMigrations.Types
open MigLib.Util

type internal ColumnValueSource =
  | SourceColumn of columnName: string
  | DefaultExpr of expr: Expr

type internal ColumnMapping =
  { targetColumn: string
    source: ColumnValueSource }

type internal TableCopyMapping =
  { sourceTable: string
    targetTable: string
    columnMappings: ColumnMapping list
    renamedColumns: (string * string) list
    addedTargetColumns: string list
    allowedDroppedSourceColumns: string list
    droppedSourceColumns: string list }

type internal SchemaDiffResult =
  { addedTables: string list
    removedTables: string list
    renamedTables: (string * string) list
    matchedTables: (string * string) list }

type internal SchemaCopyPlan =
  { diff: SchemaDiffResult
    tableMappings: TableCopyMapping list }

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

let private isRequiredColumn (column: ColumnDef) =
  isPrimaryKeyColumn column
  || hasConstraint
    (function
    | NotNull -> true
    | _ -> false)
    column

let private detectRenamedTables (removedTables: CreateTable list) (addedTables: CreateTable list) =
  let removedByName =
    removedTables |> List.map (fun table -> table.name, table) |> Map.ofList

  addedTables
  |> List.choose (fun targetTable ->
    match targetTable.previousName with
    | Some sourceName when removedByName.ContainsKey sourceName -> Some(sourceName, targetTable.name)
    | _ -> None)
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

let private isCopyCompatible (sourceColumn: ColumnDef) (targetColumn: ColumnDef) =
  sourceColumn.columnType = targetColumn.columnType
  && (not (isRequiredColumn targetColumn) || isRequiredColumn sourceColumn)
  && (not (isPrimaryKeyColumn targetColumn) || isPrimaryKeyColumn sourceColumn)
  && (not (isForeignKeyColumn targetColumn) || isForeignKeyColumn sourceColumn)

let internal buildTableCopyMapping
  (sourceTable: CreateTable)
  (targetTable: CreateTable)
  : Result<TableCopyMapping, string> =
  let sourceColumnsByName =
    sourceTable.columns
    |> List.map (fun column -> column.name, column)
    |> Map.ofList

  let usedSourceColumns = HashSet<string> StringComparer.OrdinalIgnoreCase
  let renamedColumns = ResizeArray<string * string>()
  let addedTargetColumns = ResizeArray<string>()

  let useSourceColumn (sourceColumn: ColumnDef) (targetColumn: ColumnDef) : Result<ColumnValueSource, string> =
    if not (isCopyCompatible sourceColumn targetColumn) then
      Error
        $"Target column '{targetTable.name}.{targetColumn.name}' is not copy-compatible with source column '{sourceTable.name}.{sourceColumn.name}'."
    elif not (usedSourceColumns.Add sourceColumn.name) then
      Error
        $"Source column '{sourceTable.name}.{sourceColumn.name}' is mapped more than once while building target table '{targetTable.name}'."
    else
      Ok(SourceColumn sourceColumn.name)

  let resolveColumnSource (targetColumn: ColumnDef) : Result<ColumnValueSource, string> =
    match Map.tryFind targetColumn.name sourceColumnsByName with
    | Some sourceColumn -> useSourceColumn sourceColumn targetColumn
    | None ->
      match targetColumn.previousName with
      | Some previousColumnName ->
        match Map.tryFind previousColumnName sourceColumnsByName with
        | None ->
          Error
            $"Target column '{targetTable.name}.{targetColumn.name}' declares PreviousName('{previousColumnName}') but source column '{sourceTable.name}.{previousColumnName}' does not exist."
        | Some sourceColumn when not (isCopyCompatible sourceColumn targetColumn) ->
          Error
            $"Target column '{targetTable.name}.{targetColumn.name}' declares PreviousName('{previousColumnName}') but the source column is not copy-compatible."
        | Some sourceColumn ->
          renamedColumns.Add(sourceColumn.name, targetColumn.name)
          useSourceColumn sourceColumn targetColumn
      | None ->
        addedTargetColumns.Add targetColumn.name

        match tryFindColumnDefault targetColumn with
        | Some expr -> Ok(DefaultExpr expr)
        | None when not (isRequiredColumn targetColumn) -> Ok(DefaultExpr(Value "NULL"))
        | None ->
          Error
            $"Target column '{targetTable.name}.{targetColumn.name}' is new and required, but it has no default and is not nullable."

  result {
    let! columnMappings =
      targetTable.columns
      |> foldResults
        (fun mappings targetColumn ->
          result {
            let! source = resolveColumnSource targetColumn

            return
              mappings
              @ [ { targetColumn = targetColumn.name
                    source = source } ]
          })
        []

    let droppedSourceColumns =
      sourceTable.columns
      |> List.map _.name
      |> List.filter (fun name -> not (usedSourceColumns.Contains name))

    let sourceColumnNames = sourceTable.columns |> List.map _.name |> Set.ofList

    let missingDeclaredDropColumns =
      targetTable.dropColumns
      |> List.filter (fun name -> not (sourceColumnNames.Contains name))

    do!
      if missingDeclaredDropColumns.IsEmpty then
        Ok()
      else
        let missing = String.concat ", " missingDeclaredDropColumns

        Error
          $"Target table '{targetTable.name}' declares DropColumn values that do not exist in the source table '{sourceTable.name}': {missing}"

    let declaredStillMappedDropColumns =
      targetTable.dropColumns |> List.filter usedSourceColumns.Contains

    do!
      if declaredStillMappedDropColumns.IsEmpty then
        Ok()
      else
        let mapped = String.concat ", " declaredStillMappedDropColumns

        Error
          $"Target table '{targetTable.name}' declares DropColumn values that are still mapped from source table '{sourceTable.name}': {mapped}"

    let declaredDropColumns =
      targetTable.dropColumns
      |> List.map (fun (name: string) -> name.ToLowerInvariant())
      |> Set.ofList

    let allowedDroppedSourceColumns, unsupportedDroppedSourceColumns =
      droppedSourceColumns
      |> List.partition (fun name -> declaredDropColumns.Contains(name.ToLowerInvariant()))

    return
      { sourceTable = sourceTable.name
        targetTable = targetTable.name
        columnMappings = columnMappings
        renamedColumns = renamedColumns |> Seq.toList
        addedTargetColumns = addedTargetColumns |> Seq.toList
        allowedDroppedSourceColumns = allowedDroppedSourceColumns
        droppedSourceColumns = unsupportedDroppedSourceColumns }
  }

let private findTableByName (tables: CreateTable list) (tableName: string) =
  tables
  |> List.find (fun table -> table.name.Equals(tableName, StringComparison.OrdinalIgnoreCase))

let private validateExplicitTableRenames (source: SqlFile) (target: SqlFile) : Result<unit, string> =
  let sourceTableNames = source.tables |> List.map _.name |> Set.ofList

  let targetsWithPreviousNames =
    target.tables
    |> List.choose (fun table -> table.previousName |> Option.map (fun previousName -> table, previousName))

  let duplicatePreviousNames =
    targetsWithPreviousNames
    |> List.groupBy snd
    |> List.filter (fun (_, entries) -> entries.Length > 1)

  if not duplicatePreviousNames.IsEmpty then
    let duplicates =
      duplicatePreviousNames |> List.map fst |> List.sort |> String.concat ", "

    Error $"Multiple target tables declare the same PreviousName value: {duplicates}"
  else
    let missingPreviousNames =
      targetsWithPreviousNames
      |> List.choose (fun (table, previousName) ->
        if sourceTableNames.Contains previousName then
          None
        else
          Some $"{table.name} -> {previousName}")

    if missingPreviousNames.IsEmpty then
      Ok()
    else
      let missing = String.concat ", " missingPreviousNames
      Error $"Target tables declare PreviousName values that do not exist in the source schema: {missing}"

let private validateNoSourceDataLoss
  (diff: SchemaDiffResult)
  (tableMappings: TableCopyMapping list)
  : Result<unit, string> =
  if not diff.removedTables.IsEmpty then
    let removedTables = String.concat ", " diff.removedTables
    Error $"Removing source tables is not supported because it would drop existing data: {removedTables}"
  else
    let droppedColumnsByTable =
      tableMappings
      |> List.choose (fun mapping ->
        if mapping.droppedSourceColumns.IsEmpty then
          None
        else
          let columns = mapping.droppedSourceColumns |> String.concat ", "
          Some $"{mapping.sourceTable}: {columns}")

    if droppedColumnsByTable.IsEmpty then
      Ok()
    else
      let droppedColumns = String.concat "; " droppedColumnsByTable
      Error $"Dropping source columns is not supported because it would drop existing data: {droppedColumns}"

let internal buildSchemaCopyPlan (source: SqlFile) (target: SqlFile) : Result<SchemaCopyPlan, string> =
  result {
    do! validateExplicitTableRenames source target

    let diff = diffSchemas source target

    let targetSortRank =
      target.tables |> List.mapi (fun index table -> table.name, index) |> Map.ofList

    let! tableMappings =
      diff.matchedTables
      |> foldResults
        (fun mappings (sourceName, targetName) ->
          result {
            let sourceTable = findTableByName source.tables sourceName
            let targetTable = findTableByName target.tables targetName
            let! mapping = buildTableCopyMapping sourceTable targetTable
            return mappings @ [ mapping ]
          })
        []

    let orderedTableMappings =
      tableMappings
      |> List.sortBy (fun mapping -> targetSortRank[mapping.targetTable])

    do! validateNoSourceDataLoss diff orderedTableMappings

    return
      { diff = diff
        tableMappings = orderedTableMappings }
  }

let private escapeSqlString (value: string) = value.Replace("'", "''")

let internal exprToSql (expr: Expr) =
  match expr with
  | String value -> $"'{escapeSqlString value}'"
  | Integer value -> string value
  | Real value -> string value
  | Value value -> value

let internal columnSourceSql (source: ColumnValueSource) =
  match source with
  | SourceColumn columnName -> columnName
  | DefaultExpr expr -> exprToSql expr
