module internal MigLib.Codegen.ViewIntrospection

open System
open Microsoft.Data.Sqlite
open MigLib.Schema.Types
open MigLib.Schema.Sql
open MigLib.TaskResult

let private columnConstraints (column: ColumnDef) =
  column.constraints
  |> List.choose (function
    | NotNull -> Some "NOT NULL"
    | PrimaryKey _ -> Some "PRIMARY KEY"
    | Autoincrement -> Some "AUTOINCREMENT"
    | _ -> None)
  |> String.concat " "

let private createTableSql (table: CreateTable) =
  let columns =
    table.columns
    |> List.map (fun column ->
      let typeName = sqlTypeStorageName column.columnType
      let constraints = columnConstraints column

      if String.IsNullOrWhiteSpace constraints then
        $"{quoteIdentifier column.name} {typeName}"
      else
        $"{quoteIdentifier column.name} {typeName} {constraints}")
    |> String.concat ", "

  $"CREATE TABLE {quoteIdentifier table.name} ({columns})"

let private createTables (conn: SqliteConnection) (tables: CreateTable list) =
  for table in tables do
    use cmd = new SqliteCommand(createTableSql table, conn)
    cmd.ExecuteNonQuery() |> ignore

let private createViews (conn: SqliteConnection) (views: CreateView list) =
  result {
    for view in views do
      use viewCmd = new SqliteCommand(view.sql, conn)

      try
        viewCmd.ExecuteNonQuery() |> ignore
      with ex ->
        return! Error $"Failed to create view {view.name}: {ex.Message}"
  }

let private readViewColumns (conn: SqliteConnection) (view: CreateView) =
  result {
    use pragmaCmd =
      new SqliteCommand($"PRAGMA table_info({quoteIdentifier view.name})", conn)

    let! reader =
      try
        Ok(pragmaCmd.ExecuteReader())
      with ex ->
        Error $"Failed to introspect view {view.name}: {ex.Message}"

    use reader = reader

    let columns = ResizeArray<ViewColumn>()

    while reader.Read() do
      let colName = reader.GetString 1
      let sqlType = reader.GetString 2 |> parseDeclaredSqlType

      let declaredColumn =
        view.declaredColumns
        |> List.tryFind (fun declared -> String.Equals(declared.name, colName, StringComparison.OrdinalIgnoreCase))

      let resolvedColumnType =
        match declaredColumn with
        | Some column -> column.columnType
        | None -> sqlType

      columns.Add
        { name = colName
          columnType = resolvedColumnType
          enumLikeDu = declaredColumn |> Option.bind _.enumLikeDu
          unitOfMeasure = declaredColumn |> Option.bind _.unitOfMeasure }

    let introspectedColumns = columns |> Seq.toList

    return
      if introspectedColumns.IsEmpty && not view.declaredColumns.IsEmpty then
        view.declaredColumns
      else
        introspectedColumns
  }

let getViewsColumns
  (tables: CreateTable list)
  (views: CreateView list)
  : Result<(CreateView * ViewColumn list) list, string> =
  result {
    use conn = new SqliteConnection "Data Source=:memory:"
    conn.Open()

    createTables conn tables
    do! createViews conn views

    return!
      views
      |> traverseResultM (fun view ->
        result {
          let! columns = readViewColumns conn view
          return view, columns
        })
  }

let getViewColumns (tables: CreateTable list) (view: CreateView) : Result<ViewColumn list, string> =
  result {
    let! viewsWithColumns = getViewsColumns tables [ view ]

    return viewsWithColumns |> List.tryHead |> Option.map snd |> Option.defaultValue []
  }
