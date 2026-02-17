module MigLib.DeclarativeMigrations.DataCopy

open System
open System.Collections.Generic
open System.Globalization
open FsToolkit.ErrorHandling
open MigLib.DeclarativeMigrations.SchemaDiff
open MigLib.DeclarativeMigrations.Types

type internal ForeignKeyMapping =
  { fkColumns: string list
    refTable: string
    refColumns: string list }

type internal TableIdentity =
  { sourceKeyColumns: string list
    targetKeyColumns: string list
    targetAutoincrementColumn: string option }

type internal TableCopyStep =
  { mapping: TableCopyMapping
    sourceTableDef: CreateTable
    targetTableDef: CreateTable
    insertColumns: string list
    foreignKeys: ForeignKeyMapping list
    identity: TableIdentity option }

type internal BulkCopyPlan =
  { schemaPlan: SchemaCopyPlan
    steps: TableCopyStep list
    sourceByTarget: Map<string, string> }

type internal IdMappingStore = Map<string, Map<string, Expr list>>

let internal emptyIdMappings: IdMappingStore = Map.empty

let private getPrimaryKeyColumns (table: CreateTable) : string list =
  let tableLevelPrimaryKey =
    table.constraints
    |> List.tryPick (function
      | PrimaryKey pk when not pk.columns.IsEmpty -> Some pk.columns
      | _ -> None)

  match tableLevelPrimaryKey with
  | Some columns -> columns
  | None ->
    table.columns
    |> List.filter (fun column ->
      column.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))
    |> List.map _.name

let private getAutoincrementPrimaryKeyColumn (table: CreateTable) : string option =
  let tableLevelPrimaryKey =
    table.constraints
    |> List.tryPick (function
      | PrimaryKey pk when not pk.columns.IsEmpty -> Some pk
      | _ -> None)

  match tableLevelPrimaryKey with
  | Some pk when pk.isAutoincrement && pk.columns.Length = 1 -> Some pk.columns.Head
  | _ ->
    table.columns
    |> List.tryPick (fun column ->
      column.constraints
      |> List.tryPick (function
        | PrimaryKey pk when pk.isAutoincrement -> Some column.name
        | _ -> None))

let private defaultExpr (columnType: SqlType) =
  match columnType with
  | SqlInteger -> Integer 0
  | SqlText -> String ""
  | SqlReal -> Real 0.0
  | SqlTimestamp -> String ""
  | SqlString -> String ""
  | SqlFlexible -> String ""

let private identityPart (value: Expr) =
  match value with
  | String text -> "s:" + text.Replace("|", "||")
  | Integer number -> $"i:{number}"
  | Real number -> $"r:{number.ToString(CultureInfo.InvariantCulture)}"
  | Value text -> "v:" + text.Replace("|", "||")

let private identityKey (values: Expr list) =
  values |> List.map identityPart |> String.concat "|"

let private getTableByName (tablesByName: Map<string, CreateTable>) (tableName: string) =
  match tablesByName.TryFind tableName with
  | Some table -> Ok table
  | None -> Error $"Table '{tableName}' was not found in schema."

let private normalizeRefColumns
  (tablesByName: Map<string, CreateTable>)
  (fk: ForeignKey)
  (fkColumnCount: int)
  : Result<string list, string> =
  if not fk.refColumns.IsEmpty then
    if fk.refColumns.Length = fkColumnCount then
      Ok fk.refColumns
    else
      Error
        $"Foreign key reference column count mismatch for '{fk.refTable}': expected {fkColumnCount}, got {fk.refColumns.Length}."
  else
    result {
      let! referencedTable = getTableByName tablesByName fk.refTable
      let referencedPrimaryKey = getPrimaryKeyColumns referencedTable

      if referencedPrimaryKey.Length = fkColumnCount then
        return referencedPrimaryKey
      else
        return!
          Error
            $"Foreign key reference column count mismatch for '{fk.refTable}': expected {fkColumnCount}, got inferred {referencedPrimaryKey.Length}."
    }

let private readColumnForeignKeys
  (tablesByName: Map<string, CreateTable>)
  (table: CreateTable)
  : Result<ForeignKeyMapping list, string> =
  table.columns
  |> foldResults
    (fun mappings column ->
      column.constraints
      |> foldResults
        (fun collected constraintDef ->
          match constraintDef with
          | ForeignKey fk when fk.columns.IsEmpty ->
            result {
              let! refColumns = normalizeRefColumns tablesByName fk 1

              return
                collected
                @ [ { fkColumns = [ column.name ]
                      refTable = fk.refTable
                      refColumns = refColumns } ]
            }
          | _ -> Ok collected)
        mappings)
    []

let private readTableForeignKeys
  (tablesByName: Map<string, CreateTable>)
  (table: CreateTable)
  : Result<ForeignKeyMapping list, string> =
  table.constraints
  |> foldResults
    (fun mappings constraintDef ->
      match constraintDef with
      | ForeignKey fk when not fk.columns.IsEmpty ->
        result {
          let! refColumns = normalizeRefColumns tablesByName fk fk.columns.Length

          return
            mappings
            @ [ { fkColumns = fk.columns
                  refTable = fk.refTable
                  refColumns = refColumns } ]
        }
      | _ -> Ok mappings)
    []

let private readForeignKeys
  (tablesByName: Map<string, CreateTable>)
  (table: CreateTable)
  : Result<ForeignKeyMapping list, string> =
  result {
    let! columnForeignKeys = readColumnForeignKeys tablesByName table
    let! tableForeignKeys = readTableForeignKeys tablesByName table
    return columnForeignKeys @ tableForeignKeys
  }

let private buildTableStep
  (sourceTablesByName: Map<string, CreateTable>)
  (targetTablesByName: Map<string, CreateTable>)
  (matchedTargets: Set<string>)
  (tableMapping: TableCopyMapping)
  : Result<TableCopyStep, string> =
  result {
    let! sourceTable = getTableByName sourceTablesByName tableMapping.sourceTable
    let! targetTable = getTableByName targetTablesByName tableMapping.targetTable

    let! targetForeignKeys = readForeignKeys targetTablesByName targetTable

    let foreignKeysForTranslation =
      targetForeignKeys |> List.filter (fun fk -> matchedTargets.Contains fk.refTable)

    let insertColumns =
      let autoincrementPrimaryKeyColumn = getAutoincrementPrimaryKeyColumn targetTable

      targetTable.columns
      |> List.map _.name
      |> List.filter (fun columnName -> Some columnName <> autoincrementPrimaryKeyColumn)

    let sourcePrimaryKeyColumns = getPrimaryKeyColumns sourceTable
    let targetPrimaryKeyColumns = getPrimaryKeyColumns targetTable

    let! identity =
      match sourcePrimaryKeyColumns, targetPrimaryKeyColumns with
      | [], [] -> Ok None
      | sourceKeys, targetKeys when sourceKeys.Length = targetKeys.Length ->
        Ok(
          Some
            { sourceKeyColumns = sourceKeys
              targetKeyColumns = targetKeys
              targetAutoincrementColumn = getAutoincrementPrimaryKeyColumn targetTable }
        )
      | sourceKeys, targetKeys ->
        Error
          $"PK mismatch for source '{sourceTable.name}' -> target '{targetTable.name}': source has {sourceKeys.Length}, target has {targetKeys.Length}"

    return
      { mapping = tableMapping
        sourceTableDef = sourceTable
        targetTableDef = targetTable
        insertColumns = insertColumns
        foreignKeys = foreignKeysForTranslation
        identity = identity }
  }

let private orderByDependencies (steps: TableCopyStep list) : TableCopyStep list =
  let rankByTarget =
    steps
    |> List.mapi (fun index step -> step.mapping.targetTable, index)
    |> Map.ofList

  let stepsByTarget =
    steps |> List.map (fun step -> step.mapping.targetTable, step) |> Map.ofList

  let dependenciesByTarget =
    steps
    |> List.map (fun step ->
      let dependencies =
        step.foreignKeys
        |> List.map _.refTable
        |> List.filter (fun refTable -> refTable <> step.mapping.targetTable)
        |> Set.ofList

      step.mapping.targetTable, dependencies)
    |> Map.ofList

  let rank tableName =
    rankByTarget.TryFind tableName |> Option.defaultValue Int32.MaxValue

  let mutable pending =
    steps |> List.map (fun step -> step.mapping.targetTable) |> Set.ofList

  let mutable completed = Set.empty<string>
  let ordered = ResizeArray<TableCopyStep>()

  while not pending.IsEmpty do
    let ready =
      pending
      |> Set.toList
      |> List.filter (fun tableName ->
        let dependencies = dependenciesByTarget[tableName]
        Set.isSubset dependencies completed)
      |> List.sortBy rank

    let next =
      match ready with
      | tableName :: _ -> tableName
      | [] -> pending |> Set.toList |> List.sortBy rank |> List.head

    ordered.Add stepsByTarget[next]
    completed <- completed.Add next
    pending <- pending.Remove next

  ordered |> Seq.toList

let internal buildBulkCopyPlan (source: SqlFile) (target: SqlFile) : Result<BulkCopyPlan, string> =
  result {
    let schemaPlan = buildSchemaCopyPlan source target

    let sourceTablesByName =
      source.tables |> List.map (fun table -> table.name, table) |> Map.ofList

    let targetTablesByName =
      target.tables |> List.map (fun table -> table.name, table) |> Map.ofList

    let matchedTargets =
      schemaPlan.tableMappings |> List.map _.targetTable |> Set.ofList

    let! rawSteps =
      schemaPlan.tableMappings
      |> foldResults
        (fun steps tableMapping ->
          buildTableStep sourceTablesByName targetTablesByName matchedTargets tableMapping
          |> Result.map (fun step -> steps @ [ step ]))
        []

    let stepsByTarget =
      rawSteps |> List.map (fun step -> step.mapping.targetTable, step) |> Map.ofList

    let! _validatedReferences =
      rawSteps
      |> foldResults
        (fun acc step ->
          step.foreignKeys
          |> foldResults
            (fun refs fk ->
              match stepsByTarget.TryFind fk.refTable with
              | Some refStep when refStep.identity.IsSome -> Ok refs
              | Some _ ->
                Error
                  $"Referenced table '{fk.refTable}' used by '{step.mapping.targetTable}' has no compatible identity mapping."
              | None -> Ok refs)
            acc)
        []

    let orderedSteps = orderByDependencies rawSteps

    let sourceByTarget =
      orderedSteps
      |> List.map (fun step -> step.mapping.targetTable, step.mapping.sourceTable)
      |> Map.ofList

    return
      { schemaPlan = schemaPlan
        steps = orderedSteps
        sourceByTarget = sourceByTarget }
  }

let private getColumnValue (row: Map<string, Expr>) (columnName: string) (context: string) : Result<Expr, string> =
  match row.TryFind columnName with
  | Some value -> Ok value
  | None -> Error $"Missing column '{columnName}' in {context} row."

let private getIdentityValues
  (row: Map<string, Expr>)
  (columns: string list)
  (context: string)
  : Result<Expr list, string> =
  columns
  |> foldResults
    (fun values columnName ->
      result {
        let! value = getColumnValue row columnName context
        return values @ [ value ]
      })
    []

let private applyForeignKeyTranslations
  (step: TableCopyStep)
  (row: Map<string, Expr>)
  (idMappings: IdMappingStore)
  : Result<Map<string, Expr>, string> =
  step.foreignKeys
  |> foldResults
    (fun currentRow fk ->
      result {
        let! oldValues = getIdentityValues currentRow fk.fkColumns $"target table '{step.mapping.targetTable}'"
        let oldKey = identityKey oldValues

        let! tableMappings =
          match idMappings.TryFind fk.refTable with
          | Some tableMap -> Ok tableMap
          | None -> Error $"No ID mappings are available yet for referenced table '{fk.refTable}'."

        let! mappedValues =
          match tableMappings.TryFind oldKey with
          | Some values -> Ok values
          | None ->
            let fkColumnsText = String.concat "," fk.fkColumns

            Error
              $"Missing ID mapping for FK '{fkColumnsText}' in '{step.mapping.targetTable}' referencing '{fk.refTable}' with key '{oldKey}'."

        if mappedValues.Length <> fk.fkColumns.Length then
          return!
            Error
              $"ID mapping shape mismatch for table '{fk.refTable}': expected {fk.fkColumns.Length} values, got {mappedValues.Length}."
        else
          let updatedRow =
            (currentRow, List.zip fk.fkColumns mappedValues)
            ||> List.fold (fun state (columnName, value) -> state.Add(columnName, value))

          return updatedRow
      })
    row

let internal projectRowForInsert
  (step: TableCopyStep)
  (sourceRow: Map<string, Expr>)
  (idMappings: IdMappingStore)
  : Result<Map<string, Expr> * string list * Expr list, string> =
  result {
    let! initialTargetRow =
      step.mapping.columnMappings
      |> foldResults
        (fun (row: Map<string, Expr>) (mapping: ColumnMapping) ->
          result {
            let! value =
              match mapping.source with
              | SourceColumn columnName ->
                getColumnValue sourceRow columnName $"source table '{step.mapping.sourceTable}'"
              | DefaultExpr expr -> Ok expr
              | TypeDefault columnType -> Ok(defaultExpr columnType)

            return row.Add(mapping.targetColumn, value)
          })
        Map.empty<string, Expr>

    let! translatedRow = applyForeignKeyTranslations step initialTargetRow idMappings

    let! insertValues =
      step.insertColumns
      |> foldResults
        (fun values columnName ->
          result {
            let! value = getColumnValue translatedRow columnName $"target table '{step.mapping.targetTable}'"
            return values @ [ value ]
          })
        []

    return translatedRow, step.insertColumns, insertValues
  }

let internal recordIdMapping
  (step: TableCopyStep)
  (sourceRow: Map<string, Expr>)
  (targetRow: Map<string, Expr>)
  (generatedTargetIdentity: Expr list option)
  (idMappings: IdMappingStore)
  : Result<IdMappingStore, string> =
  match step.identity with
  | None -> Ok idMappings
  | Some identity ->
    result {
      let! sourceIdentity =
        getIdentityValues sourceRow identity.sourceKeyColumns $"source table '{step.mapping.sourceTable}'"

      let! targetIdentity =
        match identity.targetAutoincrementColumn, generatedTargetIdentity with
        | Some _, Some values ->
          if values.Length = identity.targetKeyColumns.Length then
            Ok values
          else
            Error
              $"Generated target identity mismatch for '{step.mapping.targetTable}': expected {identity.targetKeyColumns.Length}, got {values.Length}."
        | Some _, None ->
          getIdentityValues targetRow identity.targetKeyColumns $"target table '{step.mapping.targetTable}'"
        | None, Some values ->
          if values.Length = identity.targetKeyColumns.Length then
            Ok values
          else
            Error
              $"Provided target identity mismatch for '{step.mapping.targetTable}': expected {identity.targetKeyColumns.Length}, got {values.Length}."
        | None, None ->
          getIdentityValues targetRow identity.targetKeyColumns $"target table '{step.mapping.targetTable}'"

      let sourceKey = identityKey sourceIdentity

      let tableMappings =
        idMappings.TryFind step.mapping.targetTable
        |> Option.defaultValue Map.empty
        |> Map.add sourceKey targetIdentity

      return idMappings.Add(step.mapping.targetTable, tableMappings)
    }
