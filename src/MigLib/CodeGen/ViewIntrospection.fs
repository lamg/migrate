/// SQLite introspection for view columns
module internal migrate.CodeGen.ViewIntrospection

open System
open Microsoft.Data.Sqlite
open FsToolkit.ErrorHandling
open migrate.DeclarativeMigrations.Types

/// Column information extracted from a view
type ViewColumn =
  { name: string
    columnType: SqlType
    isNullable: bool }

/// Extract column information from a view by creating it in a temporary database
let getViewColumns (tables: CreateTable list) (view: CreateView) : Result<ViewColumn list, string> =
  result {
    use conn = new SqliteConnection("Data Source=:memory:")
    conn.Open()

    // Create all tables first (views depend on them)
    for table in tables do
      let createTableSql =
        let columns =
          table.columns
          |> List.map (fun col ->
            let typeName =
              match col.columnType with
              | SqlInteger -> "INTEGER"
              | SqlText -> "TEXT"
              | SqlReal -> "REAL"
              | SqlTimestamp -> "TIMESTAMP"
              | SqlString -> "TEXT"
              | SqlFlexible -> "TEXT"

            let constraints =
              col.constraints
              |> List.choose (fun c ->
                match c with
                | NotNull -> Some "NOT NULL"
                | PrimaryKey _ -> Some "PRIMARY KEY"
                | Autoincrement -> Some "AUTOINCREMENT"
                | _ -> None)
              |> String.concat " "

            if String.IsNullOrWhiteSpace constraints then
              $"{col.name} {typeName}"
            else
              $"{col.name} {typeName} {constraints}")
          |> String.concat ", "

        $"CREATE TABLE {table.name} ({columns})"

      use cmd = new SqliteCommand(createTableSql, conn)
      cmd.ExecuteNonQuery() |> ignore

    // Create the view
    let viewSql = view.sqlTokens |> String.concat " "

    use viewCmd = new SqliteCommand(viewSql, conn)

    try
      viewCmd.ExecuteNonQuery() |> ignore
    with ex ->
      return! Error $"Failed to create view {view.name}: {ex.Message}"

    // Get column information using PRAGMA
    use pragmaCmd = new SqliteCommand($"PRAGMA table_info({view.name})", conn)
    use reader = pragmaCmd.ExecuteReader()

    let columns = ResizeArray<ViewColumn>()

    while reader.Read() do
      let colName = reader.GetString 1 // column 1 is 'name'
      let colType = reader.GetString 2 // column 2 is 'type'
      let notNull = reader.GetInt32 3 // column 3 is 'notnull' (1 or 0)

      let sqlType =
        match colType.ToUpperInvariant() with
        | t when t.Contains "INT" -> SqlInteger
        | t when t.Contains "TEXT" || t.Contains "CHAR" || t.Contains "CLOB" -> SqlText
        | t when t.Contains "REAL" || t.Contains "FLOA" || t.Contains "DOUB" -> SqlReal
        | t when t.Contains "TIME" || t.Contains "DATE" -> SqlTimestamp
        | _ -> SqlFlexible

      columns.Add
        { name = colName
          columnType = sqlType
          isNullable = notNull = 0 }

    return columns |> Seq.toList
  }
