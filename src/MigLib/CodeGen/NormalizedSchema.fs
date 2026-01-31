/// Module for detecting normalized database schemas with extension tables.
/// Extension tables are detected by naming convention and FK/PK relationships
/// to generate F# discriminated unions instead of option types.
module internal migrate.CodeGen.NormalizedSchema

open migrate.DeclarativeMigrations.Types

/// Check if a table has any nullable columns (columns without NOT NULL constraint)
let hasNullableColumns (table: CreateTable) : bool =
  table.columns |> List.exists TypeGenerator.isColumnNullable

/// Get the primary key column name(s) from a table
let private getPrimaryKeyColumns (table: CreateTable) : string list =
  // First check for table-level primary keys
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  match tableLevelPk with
  | Some cols -> cols
  | None ->
    // Check for column-level primary keys
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))
    |> List.map (fun col -> col.name)

/// Get foreign key information from a table
let private getForeignKeyInfo (table: CreateTable) : ForeignKey list =
  // Column-level foreign keys
  let columnFks =
    table.columns
    |> List.choose (fun col ->
      col.constraints
      |> List.tryPick (fun c ->
        match c with
        | ForeignKey fk ->
          Some
            { columns = [ col.name ]
              refTable = fk.refTable
              refColumns = fk.refColumns }
        | _ -> None))

  // Table-level foreign keys
  let tableFks =
    table.constraints
    |> List.choose (fun c ->
      match c with
      | ForeignKey fk -> Some fk
      | _ -> None)

  columnFks @ tableFks

/// Check if a table's primary key is also a foreign key to another table.
/// This is the key indicator of a 1:1 extension relationship.
let private isPrimaryKeyAlsoForeignKey (table: CreateTable) : (string * ForeignKey) option =
  let pkCols = getPrimaryKeyColumns table

  match pkCols with
  | [ singlePk ] ->
    // Single-column PK - check if it's also a FK
    let fks = getForeignKeyInfo table

    fks
    |> List.tryFind (fun fk -> fk.columns = [ singlePk ])
    |> Option.map (fun fk -> singlePk, fk)
  | _ ->
    // Composite PK or no PK - not an extension table pattern
    None

/// Extract the aspect name from an extension table name.
/// Given base table "student" and extension "student_address", returns "address".
/// Given base table "student" and extension "student_email_phone", returns "email_phone".
let private extractAspectName (baseName: string) (extensionName: string) : string option =
  let prefix = baseName + "_"

  if extensionName.StartsWith prefix && extensionName.Length > prefix.Length then
    Some(extensionName.Substring prefix.Length)
  else
    None

/// Check if a table is a potential extension table for a given base table.
/// Returns Some ExtensionTable if it matches all criteria, None otherwise.
let private tryMatchExtensionTable (baseTable: CreateTable) (potentialExtension: CreateTable) : ExtensionTable option =
  // Check 1: Extension table should not have nullable columns
  if hasNullableColumns potentialExtension then
    None
  else
    // Check 2: Name should follow pattern {base_table}_{aspect}
    match extractAspectName baseTable.name potentialExtension.name with
    | None -> None
    | Some aspectName ->
      // Check 3: Extension's PK should be a FK to the base table
      match isPrimaryKeyAlsoForeignKey potentialExtension with
      | None -> None
      | Some(fkCol, fk) ->
        // Check 4: FK should reference the base table
        if fk.refTable = baseTable.name then
          Some
            { table = potentialExtension
              aspectName = aspectName
              fkColumn = fkCol }
        else
          None

/// Find all extension tables for a given base table from a list of tables.
let findExtensionTables (baseTable: CreateTable) (allTables: CreateTable list) : ExtensionTable list =
  allTables
  |> List.filter (fun t -> t.name <> baseTable.name)
  |> List.choose (tryMatchExtensionTable baseTable)

/// Detect normalized tables from a list of tables.
/// Returns a list of NormalizedTable records for tables that:
/// 1. Have no nullable columns (all columns are NOT NULL)
/// 2. Have at least one extension table detected
///
/// Tables with nullable columns or no extensions are NOT included in the result.
let detectNormalizedTables (tables: CreateTable list) : NormalizedTable list =
  tables
  |> List.choose (fun table ->
    // Skip tables with nullable columns - they use option types
    if hasNullableColumns table then
      None
    else
      let extensions = findExtensionTables table tables

      // Only include if there are extensions
      match extensions with
      | [] -> None
      | _ ->
        Some
          { baseTable = table
            extensions = extensions })

/// Classify tables into normalized (with DU) and regular (with options) categories.
/// Returns a tuple of (normalized tables, regular tables).
let classifyTables (tables: CreateTable list) : NormalizedTable list * CreateTable list =
  let normalized = detectNormalizedTables tables

  // Get set of all table names that are part of normalized structures
  let normalizedTableNames =
    normalized
    |> List.collect (fun n -> n.baseTable.name :: (n.extensions |> List.map (fun e -> e.table.name)))
    |> Set.ofList

  // Regular tables are those not part of any normalized structure
  let regular =
    tables |> List.filter (fun t -> not (Set.contains t.name normalizedTableNames))

  normalized, regular

/// Format a validation error with a helpful suggestion for fixing it.
let formatError (error: NormalizedSchemaError) : string =
  match error with
  | NullableColumnsDetected(table, columns) ->
    let columnList = columns |> String.concat ", "

    $"Table '{table}' has nullable columns: {columnList}\n"
    + "  Suggestion: Add NOT NULL constraint to these columns:\n"
    + (columns
       |> List.map (fun col -> $"    ALTER TABLE {table} ALTER COLUMN {col} SET NOT NULL;")
       |> String.concat "\n")

  | InvalidForeignKey(extension, expected, reason) ->
    $"Extension table '{extension}' has invalid foreign key to '{expected}': {reason}\n"
    + $"  Suggestion: Ensure the table has a FOREIGN KEY constraint referencing {expected}(id)"

  | InvalidNaming(table, expected) ->
    $"Table '{table}' doesn't follow naming convention\n"
    + $"  Expected: '{expected}_<aspect>' (e.g., '{expected}_address', '{expected}_email_phone')\n"
    + $"  Suggestion: Rename the table to follow the pattern: '{expected}_<aspect>'"

  | ForeignKeyNotPrimaryKey(extension, fkColumn) ->
    $"Extension table '{extension}' has FK column '{fkColumn}' that is not the PRIMARY KEY\n"
    + "  For 1:1 relationships, the FK must also be the PK (enforces at most one extension per base record)\n"
    + $"  Suggestion: Make '{fkColumn}' the PRIMARY KEY of table '{extension}'"

  | DuplicateColumnNames(extension, baseTable, columns) ->
    let columnList = columns |> String.concat ", "

    $"Extension table '{extension}' has columns with same names as base table '{baseTable}': {columnList}\n"
    + "  F# discriminated unions cannot have duplicate field names in a single case.\n"
    + $"  Suggestion: Rename the duplicate columns in '{extension}' to be unique, e.g.:\n"
    + (columns
       |> List.map (fun col -> $"    {col} -> {extension}_{col}")
       |> String.concat "\n")

/// Get all nullable columns from a table.
let private getNullableColumns (table: CreateTable) : string list =
  table.columns
  |> List.filter TypeGenerator.isColumnNullable
  |> List.map (fun col -> col.name)

/// Validate that a potential extension table can be used for normalized schema generation.
/// Returns Ok(ExtensionTable) if valid, or Error(NormalizedSchemaError) if invalid.
let private validateExtensionTable
  (baseTable: CreateTable)
  (potentialExtension: CreateTable)
  : Result<ExtensionTable option, NormalizedSchemaError> =
  // Check 1: Name should follow pattern {base_table}_{aspect}
  match extractAspectName baseTable.name potentialExtension.name with
  | None -> Ok None // Not following pattern, just not an extension (not an error)
  | Some aspectName ->
    // Check 2: Extension table should not have nullable columns
    let nullableCols = getNullableColumns potentialExtension

    if not (List.isEmpty nullableCols) then
      Error(NullableColumnsDetected(potentialExtension.name, nullableCols))
    else
      // Check 3: Extension's PK should be a FK to the base table
      match isPrimaryKeyAlsoForeignKey potentialExtension with
      | None ->
        Error(
          ForeignKeyNotPrimaryKey(
            potentialExtension.name,
            (getPrimaryKeyColumns potentialExtension |> String.concat ", ")
          )
        )
      | Some(fkCol, fk) ->
        // Check 4: FK should reference the base table
        if fk.refTable <> baseTable.name then
          Error(
            InvalidForeignKey(
              potentialExtension.name,
              baseTable.name,
              $"FK references '{fk.refTable}' instead of '{baseTable.name}'"
            )
          )
        else
          // Check 5: Extension columns (excluding FK) should not have same names as base columns
          let baseColumnNames = baseTable.columns |> List.map (fun c -> c.name) |> Set.ofList

          let extensionColumnNames =
            potentialExtension.columns
            |> List.filter (fun c -> c.name <> fkCol)
            |> List.map (fun c -> c.name)

          let duplicates =
            extensionColumnNames
            |> List.filter (fun name -> Set.contains name baseColumnNames)

          if not (List.isEmpty duplicates) then
            Error(DuplicateColumnNames(potentialExtension.name, baseTable.name, duplicates))
          else
            Ok(
              Some
                { table = potentialExtension
                  aspectName = aspectName
                  fkColumn = fkCol }
            )

/// Validate and find all extension tables for a given base table.
/// Returns Ok(extensions) or Error(validation errors).
let validateExtensionTables
  (baseTable: CreateTable)
  (allTables: CreateTable list)
  : Result<ExtensionTable list, NormalizedSchemaError list> =
  let results =
    allTables
    |> List.filter (fun t -> t.name <> baseTable.name)
    |> List.map (validateExtensionTable baseTable)

  // Separate successes and failures
  let errors =
    results
    |> List.choose (fun r ->
      match r with
      | Error e -> Some e
      | _ -> None)

  let extensions =
    results
    |> List.choose (fun r ->
      match r with
      | Ok(Some ext) -> Some ext
      | _ -> None)

  if List.isEmpty errors then Ok extensions else Error errors

/// Detect and validate normalized tables from a list of tables.
/// Returns Ok(normalized tables) or Error(validation errors).
let validateNormalizedTables (tables: CreateTable list) : Result<NormalizedTable list, NormalizedSchemaError list> =
  let results =
    tables
    |> List.map (fun table ->
      // Check if base table has nullable columns
      let nullableCols = getNullableColumns table

      if not (List.isEmpty nullableCols) then
        // Skip tables with nullable columns - they use option types (not an error)
        Ok None
      else
        match validateExtensionTables table tables with
        | Error errors -> Error errors
        | Ok extensions ->
          // Only include if there are extensions
          match extensions with
          | [] -> Ok None
          | _ ->
            Ok(
              Some
                { baseTable = table
                  extensions = extensions }
            ))

  // Separate successes and failures
  let allErrors =
    results
    |> List.choose (fun r ->
      match r with
      | Error errors -> Some errors
      | _ -> None)
    |> List.concat

  let normalized =
    results
    |> List.choose (fun r ->
      match r with
      | Ok(Some n) -> Some n
      | _ -> None)

  if List.isEmpty allErrors then
    Ok normalized
  else
    Error allErrors
