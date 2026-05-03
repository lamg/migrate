module internal MigLib.Migrate.Planning

open System
open System.Threading.Tasks

open MigLib.Resolution.ProjectState
open MigLib.Schema.Types
open MigLib.Types
open MigLib.TaskResult

type MigrationPlan =
  { sourceSchema: SqlFile option
    targetSchema: SqlFile
    result: PlanResult }

let private describeSqlType sqlType =
  match sqlType with
  | SqlInteger -> "INTEGER"
  | SqlText -> "TEXT"
  | SqlReal -> "REAL"
  | SqlTimestamp -> "TIMESTAMP"
  | SqlString -> "STRING"

let private joinOrNone values =
  match values with
  | [] -> "none"
  | _ -> String.concat ", " values

let private effectiveTableSourceName (table: CreateTable) =
  defaultArg table.previousName table.name

let private effectiveColumnSourceName (column: ColumnDef) =
  defaultArg column.previousName column.name

let private hasColumnConstraint (predicate: ColumnConstraint -> bool) (column: ColumnDef) =
  column.constraints |> List.exists predicate

let private canPopulateAddedColumn (column: ColumnDef) =
  let hasDefault =
    column
    |> hasColumnConstraint (function
      | Default _ -> true
      | _ -> false)

  let isNotNull =
    column
    |> hasColumnConstraint (function
      | NotNull -> true
      | _ -> false)

  hasDefault || not isNotNull

let private findDuplicateMappings objectType mappings =
  mappings
  |> List.groupBy snd
  |> List.choose (fun (sourceName, targets) ->
    match targets with
    | []
    | [ _ ] -> None
    | values ->
      let targetNames = values |> List.map fst |> joinOrNone
      Some $"Multiple target {objectType}s map from source {objectType} '{sourceName}': {targetNames}.")

let private compareColumn (tableName: string) (sourceColumnByName: Map<string, ColumnDef>) (targetColumn: ColumnDef) =
  let sourceColumnName = effectiveColumnSourceName targetColumn

  match sourceColumnByName.TryFind sourceColumnName with
  | None ->
    if canPopulateAddedColumn targetColumn then
      Ok [ $"added column: {tableName}.{targetColumn.name}" ]
    else
      Error
        [ $"Column '{tableName}.{targetColumn.name}' is required in target but missing from source and has no default." ]
  | Some sourceColumn ->
    if sourceColumn.columnType = targetColumn.columnType then
      if sourceColumnName = targetColumn.name then
        Ok []
      else
        Ok [ $"renamed column: {tableName}.{sourceColumnName}->{targetColumn.name}" ]
    else
      let sourceType = describeSqlType sourceColumn.columnType
      let targetType = describeSqlType targetColumn.columnType
      Error [ $"Column '{tableName}.{targetColumn.name}' changes type from {sourceType} to {targetType}." ]

let private compareColumns (sourceTable: CreateTable) (targetTable: CreateTable) =
  let sourceColumnByName =
    sourceTable.columns
    |> List.map (fun column -> column.name, column)
    |> Map.ofList

  let targetColumnMappings =
    targetTable.columns
    |> List.map (fun column -> column.name, effectiveColumnSourceName column)

  let duplicateMappings = findDuplicateMappings "column" targetColumnMappings

  let comparedColumns =
    targetTable.columns
    |> List.map (compareColumn targetTable.name sourceColumnByName)

  let supported =
    comparedColumns
    |> List.collect (function
      | Ok lines -> lines
      | Error _ -> [])

  let unsupported =
    duplicateMappings
    @ (comparedColumns
       |> List.collect (function
         | Error lines -> lines
         | Ok _ -> []))

  let targetSourceColumnNames =
    targetTable.columns |> List.map effectiveColumnSourceName |> Set.ofList


  let droppedColumns =
    sourceTable.columns
    |> List.choose (fun column ->
      if targetSourceColumnNames.Contains column.name then
        None
      else
        Some $"dropped column: {targetTable.name}.{column.name}")

  supported @ droppedColumns, unsupported

let private compareTable (sourceTableByName: Map<string, CreateTable>) (targetTable: CreateTable) =
  let sourceTableName = effectiveTableSourceName targetTable

  match sourceTableByName.TryFind sourceTableName with
  | None -> [ $"added table: {targetTable.name}" ], []
  | Some sourceTable ->
    let tableRename =
      if sourceTableName = targetTable.name then
        []
      else
        [ $"renamed table: {sourceTableName}->{targetTable.name}" ]

    let columnSupported, columnUnsupported = compareColumns sourceTable targetTable
    tableRename @ columnSupported, columnUnsupported

let private analyzeSchemaDifferences (sourceSchema: SqlFile) (targetSchema: SqlFile) =
  let sourceTableByName =
    sourceSchema.tables |> List.map (fun table -> table.name, table) |> Map.ofList

  let targetTableMappings =
    targetSchema.tables
    |> List.map (fun table -> table.name, effectiveTableSourceName table)

  let duplicateMappings = findDuplicateMappings "table" targetTableMappings

  let tableComparisons =
    targetSchema.tables |> List.map (compareTable sourceTableByName)

  let targetSourceTableNames =
    targetSchema.tables |> List.map effectiveTableSourceName |> Set.ofList

  let removedTables =
    sourceSchema.tables
    |> List.choose (fun table ->
      if targetSourceTableNames.Contains table.name then
        None
      else
        Some $"removed table: {table.name}")

  let supported = removedTables @ (tableComparisons |> List.collect fst)

  let unsupported = duplicateMappings @ (tableComparisons |> List.collect snd)

  supported, unsupported

let buildPlan (reportProgress: ProgReport) (project: MigProject) : Task<Result<MigrationPlan, MigError>> =
  taskResult {
    let! (projectState: ResolvedMigProject) = resolveProjectState project
    let targetSchema = project.targetSchema
    do! reportProgress $"Planning migration to target database: {projectState.targetDbPath}"
    let sourceSchema = projectState.sourceSchema

    let supportedDifferences, unsupportedDifferences =
      match sourceSchema with
      | None -> [ "no source database found" ], []
      | Some oldSchema -> analyzeSchemaDifferences oldSchema targetSchema

    return
      { sourceSchema = sourceSchema
        targetSchema = targetSchema
        result =
          { sourceDbPath = projectState.sourceDbPath
            targetDbPath = projectState.targetDbPath
            canMigrate = unsupportedDifferences.IsEmpty
            supportedDifferences = supportedDifferences
            unsupportedDifferences = unsupportedDifferences } }
  }
