module internal Mig.CodeGen.NormalizedQueryGeneratorInsertSelect

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon

let private generateBaseCase (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n          "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT into base table
          use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
          {asyncParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd.ExecuteNonQueryAsync()
          use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
          let! lastId = lastIdCmd.ExecuteScalarAsync()
          let {baseTable.name}Id = lastId |> unbox<int64>
          return Ok {baseTable.name}Id"""

let private generateBaseCaseInsertOrIgnore (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertOrIgnoreSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n          "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT OR IGNORE into base table
          use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
          {asyncParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! rows = cmd.ExecuteNonQueryAsync()
          if rows = 0 then
            return Ok None
          else
            use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! lastId = lastIdCmd.ExecuteScalarAsync()
            let {baseTable.name}Id = lastId |> unbox<int64>
            return Ok (Some {baseTable.name}Id)"""

let private generateExtensionCase (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd1" |> String.concat "\n          "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd2"
    |> String.concat "\n          "

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Two inserts in same transaction (atomic)
          use cmd1 = new SqliteCommand("{baseInsertSql}", tx.Connection, tx)
          {asyncBaseParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd1.ExecuteNonQueryAsync()

          use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
          let! lastId = lastIdCmd.ExecuteScalarAsync()
          let {baseTable.name}Id = lastId |> unbox<int64>

          use cmd2 = new SqliteCommand("INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})", tx.Connection, tx)
          cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {extensionFkValueExpr}) |> ignore
          {asyncExtensionParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd2.ExecuteNonQueryAsync()
          return Ok {baseTable.name}Id"""

let private generateExtensionCaseInsertOrIgnore
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (typeName: string)
  : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertOrIgnoreSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd1" |> String.concat "\n          "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd2"
    |> String.concat "\n          "

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Base INSERT OR IGNORE then extension INSERT
          use cmd1 = new SqliteCommand("{baseInsertSql}", tx.Connection, tx)
          {asyncBaseParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! rows = cmd1.ExecuteNonQueryAsync()
          if rows = 0 then
            return Ok None
          else
            use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! lastId = lastIdCmd.ExecuteScalarAsync()
            let {baseTable.name}Id = lastId |> unbox<int64>

            use cmd2 = new SqliteCommand("INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})", tx.Connection, tx)
            cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {extensionFkValueExpr}) |> ignore
            {asyncExtensionParamBindings}
            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd2.ExecuteNonQueryAsync()
            return Ok (Some {baseTable.name}Id)"""

let generateInsert (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let baseCase = generateBaseCase normalized.baseTable typeName

  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCase normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member Insert (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

let generateInsertOrIgnore (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let baseCase = generateBaseCaseInsertOrIgnore normalized.baseTable typeName

  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCaseInsertOrIgnore normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member InsertOrIgnore (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64 option, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

let generateGetAll (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}"

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""

let generateGetById (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
    let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

    let leftJoins =
      if normalized.extensions.IsEmpty then
        ""
      else
        "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

    let whereClause =
      pks
      |> List.map (fun pk -> $"{normalized.baseTable.name}.{pk.name} = @{pk.name}")
      |> String.concat " AND "

    let getSql =
      $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk ->
        $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {TypeGenerator.toDbValueExpr pk pk.name}) |> ignore")
      |> String.concat "\n        "

    let caseSelection =
      generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

    Some
      $"""  static member SelectById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""

let generateGetOne (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         LIMIT 1"

  let caseSelection =
    generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""
