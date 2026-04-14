namespace Mig

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open DeclarativeMigrations.Types
open Mig.HotMigrationPrimitives

module internal HotMigrationSchemaIntrospection =
  type TableInfoRow =
    { name: string
      declaredType: string
      isNotNull: bool
      defaultSql: string option
      primaryKeyOrder: int }

  type ForeignKeyRow =
    { id: int
      seq: int
      refTable: string
      fromColumn: string
      toColumn: string option
      onUpdate: string
      onDelete: string }

  let readTableList
    (connection: SqliteConnection)
    (excludedTables: Set<string>)
    : Task<Result<(string * string option) list, SqliteException>> =
    task {
      try
        use cmd =
          createCommand
            connection
            None
            "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"

        use! reader = cmd.ExecuteReaderAsync()
        let tables = ResizeArray<string * string option>()
        let mutable keepReading = true

        while keepReading do
          let! hasRow = reader.ReadAsync()

          if hasRow then
            let tableName = reader.GetString 0

            if not (excludedTables.Contains tableName) then
              let sql = if reader.IsDBNull 1 then None else Some(reader.GetString 1)
              tables.Add(tableName, sql)
          else
            keepReading <- false

        return Ok(tables |> Seq.toList)
      with :? SqliteException as ex ->
        return Error ex
    }

  let readTableInfoRows
    (connection: SqliteConnection)
    (tableName: string)
    : Task<Result<TableInfoRow list, SqliteException>> =
    task {
      try
        use cmd =
          createCommand connection None $"PRAGMA table_info({quoteIdentifier tableName});"

        use! reader = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<TableInfoRow>()
        let mutable keepReading = true

        while keepReading do
          let! hasRow = reader.ReadAsync()

          if hasRow then
            rows.Add
              { name = reader.GetString 1
                declaredType = if reader.IsDBNull 2 then "" else reader.GetString 2
                isNotNull = reader.GetInt32 3 = 1
                defaultSql = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
                primaryKeyOrder = reader.GetInt32 5 }
          else
            keepReading <- false

        return Ok(rows |> Seq.toList)
      with :? SqliteException as ex ->
        return Error ex
    }

  let readForeignKeyRows
    (connection: SqliteConnection)
    (tableName: string)
    : Task<Result<ForeignKeyRow list, SqliteException>> =
    task {
      try
        use cmd =
          createCommand connection None $"PRAGMA foreign_key_list({quoteIdentifier tableName});"

        use! reader = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<ForeignKeyRow>()
        let mutable keepReading = true

        while keepReading do
          let! hasRow = reader.ReadAsync()

          if hasRow then
            rows.Add
              { id = reader.GetInt32 0
                seq = reader.GetInt32 1
                refTable = reader.GetString 2
                fromColumn = reader.GetString 3
                toColumn = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
                onUpdate = if reader.IsDBNull 5 then "" else reader.GetString 5
                onDelete = if reader.IsDBNull 6 then "" else reader.GetString 6 }
          else
            keepReading <- false

        return Ok(rows |> Seq.toList)
      with :? SqliteException as ex ->
        return Error ex
    }

  let addColumnConstraint
    (columnName: string)
    (constraintDef: ColumnConstraint)
    (columns: ColumnDef list)
    : ColumnDef list =
    columns
    |> List.map (fun column ->
      if column.name.Equals(columnName, StringComparison.OrdinalIgnoreCase) then
        { column with
            constraints = column.constraints @ [ constraintDef ] }
      else
        column)

  let buildForeignKeyConstraint (rows: ForeignKeyRow list) (columns: string list) : ColumnConstraint =
    let orderedRows = rows |> List.sortBy _.seq
    let head = orderedRows.Head

    let refColumns =
      orderedRows
      |> List.choose _.toColumn
      |> List.filter (String.IsNullOrWhiteSpace >> not)

    ForeignKey
      { columns = columns
        refTable = head.refTable
        refColumns = refColumns
        onDelete = parseFkAction head.onDelete
        onUpdate = parseFkAction head.onUpdate }

  let buildTableDefinition
    (tableName: string)
    (createSql: string option)
    (tableInfoRows: TableInfoRow list)
    (foreignKeyRows: ForeignKeyRow list)
    : CreateTable =
    let primaryKeys =
      tableInfoRows
      |> List.filter (fun row -> row.primaryKeyOrder > 0)
      |> List.sortBy _.primaryKeyOrder
      |> List.map _.name

    let hasAutoincrement =
      match createSql with
      | None -> false
      | Some sql -> sql.ToUpperInvariant().Contains "AUTOINCREMENT"

    let singlePrimaryKey =
      if primaryKeys.Length = 1 then
        Some primaryKeys.Head
      else
        None

    let mutable columns =
      tableInfoRows
      |> List.map (fun row ->
        let constraints = ResizeArray<ColumnConstraint>()

        match singlePrimaryKey with
        | Some pk when pk.Equals(row.name, StringComparison.OrdinalIgnoreCase) ->
          constraints.Add(
            PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = hasAutoincrement }
          )
        | _ -> ()

        if row.isNotNull then
          constraints.Add NotNull

        match row.defaultSql with
        | Some defaultSql when not (String.IsNullOrWhiteSpace defaultSql) ->
          constraints.Add(Default(parseDefaultExpr defaultSql))
        | _ -> ()

        { name = row.name
          previousName = None
          columnType = parseSqlType row.declaredType
          constraints = constraints |> Seq.toList
          enumLikeDu = None
          unitOfMeasure = None })

    let fkGroups = foreignKeyRows |> List.groupBy _.id

    let tableConstraints =
      fkGroups
      |> List.choose (fun (_, rows) ->
        let orderedRows = rows |> List.sortBy _.seq

        if orderedRows.Length > 1 then
          let fkColumns = orderedRows |> List.map _.fromColumn
          Some(buildForeignKeyConstraint orderedRows fkColumns)
        else
          None)

    for _, rows in fkGroups do
      let orderedRows = rows |> List.sortBy _.seq

      if orderedRows.Length = 1 then
        let row = orderedRows.Head
        let constraintDef = buildForeignKeyConstraint orderedRows []
        columns <- addColumnConstraint row.fromColumn constraintDef columns

    let tablePrimaryKeyConstraint =
      if primaryKeys.Length > 1 then
        [ PrimaryKey
            { constraintName = None
              columns = primaryKeys
              isAutoincrement = false } ]
      else
        []

    { name = tableName
      previousName = None
      dropColumns = []
      columns = columns
      constraints = tablePrimaryKeyConstraint @ tableConstraints
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = []
      selectOneAnnotations = []
      insertOrIgnoreAnnotations = []
      deleteAllAnnotations = []
      upsertAnnotations = [] }

  let loadSchemaFromDatabase
    (connection: SqliteConnection)
    (excludedTables: Set<string>)
    : Task<Result<SqlFile, SqliteException>> =
    task {
      let! tableListResult = readTableList connection excludedTables

      match tableListResult with
      | Error ex -> return Error ex
      | Ok tableList ->
        let tables = ResizeArray<CreateTable>()

        let isOk =
          function
          | Ok _ -> true
          | Error _ -> false

        let mutable index = 0
        let mutable result: Result<SqlFile, SqliteException> = Ok emptyFile

        while index < tableList.Length && isOk result do
          let tableName, createSql = tableList[index]
          let! tableInfoResult = readTableInfoRows connection tableName

          match tableInfoResult with
          | Error ex -> result <- Error ex
          | Ok tableInfoRows ->
            let! fkRowsResult = readForeignKeyRows connection tableName

            match fkRowsResult with
            | Error ex -> result <- Error ex
            | Ok fkRows ->
              let table = buildTableDefinition tableName createSql tableInfoRows fkRows
              tables.Add table
              index <- index + 1

        match result with
        | Error ex -> return Error ex
        | Ok _ ->
          return
            Ok
              { emptyFile with
                  tables = tables |> Seq.toList }
    }
