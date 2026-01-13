/// Module for generating F# discriminated union types from normalized database schemas.
/// Generates two DUs per normalized table: New{Type} for inserts and {Type} for queries.
module internal migrate.CodeGen.NormalizedTypeGenerator

open migrate.DeclarativeMigrations.Types

/// Get columns that should be included in the "New" type (excludes auto-increment PKs)
let private getInsertColumns (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    // Exclude auto-increment primary key columns
    not (
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)
    ))

/// Generate an anonymous record field string like "Name: string"
let private generateAnonField (col: ColumnDef) : string =
  let fieldName = TypeGenerator.toPascalCase col.name
  let isNullable = TypeGenerator.isColumnNullable col
  let fsharpType = TypeGenerator.mapSqlType col.columnType isNullable
  $"{fieldName}: {fsharpType}"

/// Generate anonymous record type string like "{| Name: string; Age: int64 |}"
let private generateAnonRecord (columns: ColumnDef list) : string =
  let fields = columns |> List.map generateAnonField |> String.concat "; "
  $"{{| {fields} |}}"

/// Convert aspect name to PascalCase for union case naming
/// e.g., "email_phone" -> "EmailPhone"
let private aspectToPascalCase (aspect: string) : string = TypeGenerator.toPascalCase aspect

/// Generate a single DU case definition
let private generateCase (caseName: string) (columns: ColumnDef list) : string =
  let anonRecord = generateAnonRecord columns
  $"  | {caseName} of {anonRecord}"

/// Generate the Base case columns (base table columns only)
let private getBaseCaseColumns (baseTable: CreateTable) (includeAutoIncrementPk: bool) : ColumnDef list =
  if includeAutoIncrementPk then
    baseTable.columns
  else
    getInsertColumns baseTable

/// Generate extension case columns (base table columns + extension columns, excluding FK)
let private getExtensionCaseColumns
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (includeAutoIncrementPk: bool)
  : ColumnDef list =
  let baseColumns = getBaseCaseColumns baseTable includeAutoIncrementPk

  // Extension columns excluding the FK column (which duplicates base PK)
  let extensionColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  baseColumns @ extensionColumns

/// Generate the "New" discriminated union type for inserts (no auto-increment PK)
let generateNewType (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"

  // Base case
  let baseColumns = getBaseCaseColumns normalized.baseTable false
  let baseCase = generateCase "Base" baseColumns

  // Extension cases
  let extensionCases =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{aspectToPascalCase ext.aspectName}"
      let columns = getExtensionCaseColumns normalized.baseTable ext false
      generateCase caseName columns)

  let allCases = baseCase :: extensionCases |> String.concat "\n"

  $"[<RequireQualifiedAccess>]\ntype {newTypeName} =\n{allCases}"

/// Generate the query discriminated union type (includes all columns including PK)
let generateQueryType (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // Base case
  let baseColumns = getBaseCaseColumns normalized.baseTable true
  let baseCase = generateCase "Base" baseColumns

  // Extension cases
  let extensionCases =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{aspectToPascalCase ext.aspectName}"
      let columns = getExtensionCaseColumns normalized.baseTable ext true
      generateCase caseName columns)

  let allCases = baseCase :: extensionCases |> String.concat "\n"

  $"[<RequireQualifiedAccess>]\ntype {typeName} =\n{allCases}"

/// Information about a field across all DU cases
type private FieldInfo =
  { Name: string
    FSharpType: string
    InAllCases: bool }

/// Collect all unique fields across all cases and determine if they're in all cases
let private collectFields (normalized: NormalizedTable) (includeAutoIncrementPk: bool) : FieldInfo list =
  // Get all cases
  let baseCaseColumns = getBaseCaseColumns normalized.baseTable includeAutoIncrementPk

  let extensionCasesColumns =
    normalized.extensions
    |> List.map (fun ext -> getExtensionCaseColumns normalized.baseTable ext includeAutoIncrementPk)

  let allCases = baseCaseColumns :: extensionCasesColumns
  let totalCases = allCases.Length

  // Build a map of field name -> (type, count of cases it appears in)
  let fieldMap =
    allCases
    |> List.collect (fun columns ->
      columns
      |> List.map (fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name
        let isNullable = TypeGenerator.isColumnNullable col
        let fsharpType = TypeGenerator.mapSqlType col.columnType isNullable
        fieldName, fsharpType))
    |> List.groupBy fst
    |> List.map (fun (fieldName, occurrences) ->
      // Get the type from first occurrence (should be consistent)
      let fsharpType = occurrences |> List.head |> snd
      let count = occurrences.Length

      fieldName,
      { Name = fieldName
        FSharpType = fsharpType
        InAllCases = count = totalCases })
    |> Map.ofList

  fieldMap |> Map.toList |> List.map snd

/// Generate a property member for a field
let private generateProperty (typeName: string) (field: FieldInfo) (normalized: NormalizedTable) : string =
  let returnType =
    if field.InAllCases then
      field.FSharpType
    else
      $"{field.FSharpType} option"

  // Generate pattern matching
  let baseCase =
    let baseColumns = getBaseCaseColumns normalized.baseTable true

    let hasField =
      baseColumns
      |> List.exists (fun col -> TypeGenerator.toPascalCase col.name = field.Name)

    if hasField then
      if field.InAllCases then
        $"    | {typeName}.Base data -> data.{field.Name}"
      else
        $"    | {typeName}.Base data -> Some data.{field.Name}"
    else
      $"    | {typeName}.Base _ -> None"

  let extensionCases =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{aspectToPascalCase ext.aspectName}"
      let columns = getExtensionCaseColumns normalized.baseTable ext true

      let hasField =
        columns
        |> List.exists (fun col -> TypeGenerator.toPascalCase col.name = field.Name)

      if hasField then
        if field.InAllCases then
          $"    | {typeName}.{caseName} data -> data.{field.Name}"
        else
          $"    | {typeName}.{caseName} data -> Some data.{field.Name}"
      else
        $"    | {typeName}.{caseName} _ -> None")

  let allCases = baseCase :: extensionCases |> String.concat "\n"

  $"  member this.{field.Name} : {returnType} =\n    match this with\n{allCases}"

/// Generate properties for the query type
let private generateProperties (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let fields = collectFields normalized true

  if fields.IsEmpty then
    ""
  else
    let properties =
      fields
      |> List.map (fun field -> generateProperty typeName field normalized)
      |> String.concat "\n\n"

    $"\ntype {typeName} with\n{properties}"

/// Generate both DU types for a normalized table with properties
let generateTypes (normalized: NormalizedTable) : string =
  let newType = generateNewType normalized
  let queryType = generateQueryType normalized
  let properties = generateProperties normalized
  $"{newType}\n\n{queryType}{properties}"
