namespace Mig

open System
open Microsoft.Data.Sqlite
open DeclarativeMigrations.DataCopy
open DeclarativeMigrations.SchemaDiff
open DeclarativeMigrations.Types
open Mig.HotMigrationPrimitives

module internal HotMigrationPlanning =
  let joinOrNone (items: string list) =
    match items with
    | [] -> "none"
    | values -> String.concat ", " values

  let formatRenamePairs (pairs: (string * string) list) =
    match pairs with
    | [] -> "none"
    | values ->
      values
      |> List.map (fun (sourceName, targetName) -> $"{sourceName}->{targetName}")
      |> String.concat ", "

  let formatTableMappingDelta (mapping: TableCopyMapping) : string option =
    let deltas =
      [ if not (mapping.sourceTable.Equals(mapping.targetTable, StringComparison.OrdinalIgnoreCase)) then
          yield $"table rename {mapping.sourceTable}->{mapping.targetTable}"

        if not mapping.renamedColumns.IsEmpty then
          let renamedColumns =
            mapping.renamedColumns
            |> List.map (fun (sourceName, targetName) -> $"{sourceName}->{targetName}")
            |> String.concat ", "

          yield $"renamed columns [{renamedColumns}]"

        if not mapping.addedTargetColumns.IsEmpty then
          let addedTargetColumns = mapping.addedTargetColumns |> String.concat ", "
          yield $"added target columns [{addedTargetColumns}]"

        if not mapping.droppedSourceColumns.IsEmpty then
          let droppedSourceColumns = mapping.droppedSourceColumns |> String.concat ", "
          yield $"dropped source columns [{droppedSourceColumns}]" ]

    match deltas with
    | [] -> None
    | changes ->
      let changeSummary = changes |> String.concat "; "
      Some $"table '{mapping.targetTable}': {changeSummary}"

  type NonTableConsistencyReport =
    { supportedLines: string list
      unsupportedLines: string list }

  let summarizeIndexDefinitions (indexes: CreateIndex list) =
    match indexes with
    | [] -> "target indexes: none"
    | values ->
      let details =
        values
        |> List.map (fun index ->
          let columns = index.columns |> String.concat ", "
          $"{index.name} ({index.table}: {columns})")
        |> String.concat ", "

      $"target indexes: {details}"

  let summarizeViewDefinitions (views: CreateView list) =
    match views with
    | [] -> "target views: none"
    | values ->
      let details =
        values
        |> List.map (fun view ->
          let dependencies = view.dependencies |> joinOrNone
          $"{view.name} (deps: {dependencies})")
        |> String.concat ", "

      $"target views: {details}"

  let summarizeTriggerDefinitions (triggers: CreateTrigger list) =
    match triggers with
    | [] -> "target triggers: none"
    | values ->
      let details =
        values
        |> List.map (fun trigger ->
          let dependencies = trigger.dependencies |> joinOrNone
          $"{trigger.name} (deps: {dependencies})")
        |> String.concat ", "

      $"target triggers: {details}"

  let detectDuplicateObjectNames (objectType: string) (names: string list) =
    names
    |> List.groupBy id
    |> List.choose (fun (name, values) ->
      if values.Length > 1 then
        Some $"Target {objectType} '{name}' is declared {values.Length} times."
      else
        None)

  let validateIndexConsistency (targetSchema: SqlFile) =
    let tablesByName =
      targetSchema.tables |> List.map (fun table -> table.name, table) |> Map.ofList

    targetSchema.indexes
    |> List.collect (fun index ->
      match tablesByName.TryFind index.table with
      | None -> [ $"Index '{index.name}' references missing table '{index.table}'." ]
      | Some tableDef ->
        let tableColumns = tableDef.columns |> List.map _.name |> Set.ofList

        let missingColumns =
          index.columns
          |> List.filter (fun columnName -> not (tableColumns.Contains columnName))

        if missingColumns.IsEmpty then
          []
        else
          let missingColumnText = missingColumns |> String.concat ", "
          [ $"Index '{index.name}' references missing columns on '{index.table}': {missingColumnText}." ])

  let validateViewConsistency (targetSchema: SqlFile) =
    let tableNames = targetSchema.tables |> List.map _.name |> Set.ofList
    let viewNames = targetSchema.views |> List.map _.name |> Set.ofList
    let knownDependencies = Set.union tableNames viewNames

    targetSchema.views
    |> List.collect (fun view ->
      let missingDependencies =
        view.dependencies
        |> List.filter (fun dependency -> not (knownDependencies.Contains dependency))

      let dependencyErrors =
        if missingDependencies.IsEmpty then
          []
        else
          let missingDependencyText = missingDependencies |> String.concat ", "
          [ $"View '{view.name}' references missing dependencies: {missingDependencyText}." ]

      let tokenErrors =
        if String.IsNullOrWhiteSpace view.sql then
          [ $"View '{view.name}' has no SQL." ]
        else
          []

      dependencyErrors @ tokenErrors)

  let validateTriggerConsistency (targetSchema: SqlFile) =
    let tableNames = targetSchema.tables |> List.map _.name |> Set.ofList
    let viewNames = targetSchema.views |> List.map _.name |> Set.ofList
    let knownDependencies = Set.union tableNames viewNames

    targetSchema.triggers
    |> List.collect (fun trigger ->
      let missingDependencies =
        trigger.dependencies
        |> List.filter (fun dependency -> not (knownDependencies.Contains dependency))

      let dependencyErrors =
        if missingDependencies.IsEmpty then
          []
        else
          let missingDependencyText = missingDependencies |> String.concat ", "
          [ $"Trigger '{trigger.name}' references missing dependencies: {missingDependencyText}." ]

      let tokenErrors =
        if String.IsNullOrWhiteSpace trigger.sql then
          [ $"Trigger '{trigger.name}' has no SQL." ]
        else
          []

      dependencyErrors @ tokenErrors)

  let analyzeNonTableConsistency (targetSchema: SqlFile) : NonTableConsistencyReport =
    let duplicateIndexNames =
      targetSchema.indexes |> List.map _.name |> detectDuplicateObjectNames "index"

    let duplicateViewNames =
      targetSchema.views |> List.map _.name |> detectDuplicateObjectNames "view"

    let duplicateTriggerNames =
      targetSchema.triggers |> List.map _.name |> detectDuplicateObjectNames "trigger"

    let unsupportedLines =
      duplicateIndexNames
      @ duplicateViewNames
      @ duplicateTriggerNames
      @ validateIndexConsistency targetSchema
      @ validateViewConsistency targetSchema
      @ validateTriggerConsistency targetSchema

    let supportedLines =
      [ summarizeIndexDefinitions targetSchema.indexes
        summarizeViewDefinitions targetSchema.views
        summarizeTriggerDefinitions targetSchema.triggers
        if unsupportedLines.IsEmpty then
          "non-table consistency checks: passed"
        else
          "non-table consistency checks: found unsupported target-schema issues" ]

    { supportedLines = supportedLines
      unsupportedLines = unsupportedLines }

  let describeSupportedDifferences (schemaPlan: SchemaCopyPlan) : string list =
    let diff = schemaPlan.diff

    let tableLevelSummary =
      [ $"added tables: {joinOrNone diff.addedTables}"
        $"removed tables: {joinOrNone diff.removedTables}"
        $"renamed tables: {formatRenamePairs diff.renamedTables}" ]

    let mappingDeltas = schemaPlan.tableMappings |> List.choose formatTableMappingDelta

    if mappingDeltas.IsEmpty then
      tableLevelSummary @ [ "column/table mapping deltas: none" ]
    else
      tableLevelSummary @ mappingDeltas

  let renderPreflightReport (supported: string list) (unsupported: string list) =
    let renderSection (header: string) (lines: string list) =
      let normalizedLines =
        match lines with
        | [] -> [ "none" ]
        | values -> values

      header :: (normalizedLines |> List.map (fun line -> $"  - {line}"))

    [ "Schema preflight report:"
      yield! renderSection "Supported differences:" supported
      yield! renderSection "Unsupported differences:" unsupported ]
    |> String.concat Environment.NewLine

  let buildCopyPlan (sourceSchema: SqlFile) (targetSchema: SqlFile) : Result<BulkCopyPlan, SqliteException> =
    let nonTableConsistency = analyzeNonTableConsistency targetSchema

    match buildSchemaCopyPlan sourceSchema targetSchema with
    | Error message ->
      let report =
        renderPreflightReport nonTableConsistency.supportedLines (nonTableConsistency.unsupportedLines @ [ message ])

      Error(toSqliteError report)
    | Ok schemaPlan ->
      let tableDifferences = describeSupportedDifferences schemaPlan
      let supportedDifferences = tableDifferences @ nonTableConsistency.supportedLines

      match buildBulkCopyPlan sourceSchema targetSchema with
      | Ok plan ->
        if nonTableConsistency.unsupportedLines.IsEmpty then
          Ok plan
        else
          let report =
            renderPreflightReport supportedDifferences nonTableConsistency.unsupportedLines

          Error(toSqliteError report)
      | Error message ->
        let unsupported = nonTableConsistency.unsupportedLines @ [ message ]
        let report = renderPreflightReport supportedDifferences unsupported
        Error(toSqliteError report)
