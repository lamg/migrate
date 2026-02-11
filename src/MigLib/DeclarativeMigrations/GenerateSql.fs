module internal migrate.DeclarativeMigrations.GenerateSql

open Types

let sepComma f xs = xs |> List.map f |> String.concat ", "
let sepSemi (xs: string list) = xs |> String.concat ";\n"

let fkActionSql =
  function
  | Cascade -> "CASCADE"
  | Restrict -> "RESTRICT"
  | NoAction -> "NO ACTION"
  | SetNull -> "SET NULL"
  | SetDefault -> "SET DEFAULT"

module Table =
  let constraintSql =
    function
    | NotNull -> "NOT NULL"
    | PrimaryKey { constraintName = n
                   columns = []
                   isAutoincrement = ia } ->
      let tail = if ia then " AUTOINCREMENT" else ""

      let head =
        match n with
        | Some name -> $"CONSTRAINT {name} "
        | None -> ""

      $"{head}PRIMARY KEY{tail}"
    | PrimaryKey xs -> $"PRIMARY KEY({sepComma id xs.columns})"
    | Autoincrement -> "AUTOINCREMENT"
    | Default(String v) -> $"DEFAULT '{v}'"
    | Default(Integer v) -> $"DEFAULT {v}"
    | Default(Real v) -> $"DEFAULT {v}"
    | Default(Value v) -> $"DEFAULT {v}"
    | Unique [] -> "UNIQUE"
    | Unique xs -> $"UNIQUE({sepComma id xs})"
    | Check expr ->
      let joined = expr |> String.concat " "
      $"CHECK ({joined})"
    | ForeignKey f ->
      let cols = f.columns |> sepComma id

      let refCols =
        if List.isEmpty f.refColumns then
          ""
        else
          $"({f.refColumns |> sepComma id})"

      let onDelete =
        f.onDelete
        |> Option.map (fun a -> $" ON DELETE {fkActionSql a}")
        |> Option.defaultValue ""

      let onUpdate =
        f.onUpdate
        |> Option.map (fun a -> $" ON UPDATE {fkActionSql a}")
        |> Option.defaultValue ""

      if List.isEmpty f.columns then
        $"REFERENCES {f.refTable}{refCols}{onDelete}{onUpdate}"
      else
        $"FOREIGN KEY({cols}) REFERENCES {f.refTable}{refCols}{onDelete}{onUpdate}"

  let defaultValueSql =
    function
    | SqlInteger -> "0"
    | SqlText -> "''"
    | SqlReal -> "0.0"
    | SqlTimestamp -> "''"
    | SqlString -> "''"
    | SqlFlexible -> "''"

  let colTypeSql =
    function
    | SqlInteger -> "integer"
    | SqlText -> "text"
    | SqlReal -> "real"
    | SqlTimestamp -> "timestamp"
    | SqlString -> "string"
    | SqlFlexible -> ""

  let columnDefSql (c: ColumnDef) =
    let constraints = c.constraints |> List.map constraintSql |> String.concat " "
    $"{c.name} {c.columnType |> colTypeSql} {constraints}"

  let constraintsSql (table: CreateTable) =
    match table.constraints with
    | [] -> ""
    | _ -> $", {table.constraints |> sepComma constraintSql}"

  let dropSql (table: string) = $"DROP TABLE {table}"

  let createSql (table: CreateTable) =
    let columns = table.columns |> sepComma columnDefSql
    let constraints = constraintsSql table
    $"CREATE TABLE {table.name}({columns}{constraints})"

  let sqlRenameTable (from: string, to_: string) = $"ALTER TABLE {from} RENAME TO {to_}"

  let recreateSql (_: CreateView list) (table: CreateTable) =
    let auxTable =
      { table with
          name = $"{table.name}_aux" }

    let createAux = auxTable |> createSql
    let auxColumns = auxTable.columns |> sepComma _.name


    createAux
    :: [ $"INSERT OR IGNORE INTO {auxTable.name}({auxColumns}) SELECT {auxColumns} FROM {table.name}"
         $"DROP TABLE {table.name}"
         $"ALTER TABLE {auxTable.name} RENAME TO {table.name}" ]

let tokensToString xs =
  match xs with
  | _ when Seq.length xs = 1 -> Seq.head xs
  | _ ->
    xs
    |> Seq.pairwise
    |> Seq.mapi (fun i (pred, curr) ->
      if i = 0 then
        $"{pred} {curr}"
      else
        match pred, curr with
        | ",", _ -> $" {curr}"
        | _, ","
        | _, "."
        | ".", _
        | _, ")"
        | _, "(" -> curr
        | _ -> $" {curr}")

    |> String.concat ""

module View =
  let createSql (view: CreateView) =
    // space is needed for differentiating keywords from identifiers
    // space is good after a comma but not needed
    view.sqlTokens |> tokensToString


  let dropSql (viewName: string) = $"DROP VIEW {viewName}"

module Trigger =
  let createSql (trigger: CreateTrigger) = trigger.sqlTokens |> tokensToString

  let dropSql (triggerName: string) = $"DROP TRIGGER {triggerName}"

module Index =
  let createSql (index: CreateIndex) =
    let cols = index.columns |> sepComma id
    $"CREATE INDEX {index.name} ON {index.table}({cols})"

  let dropSql (indexName: string) = $"DROP INDEX {indexName}"

module Column =
  let sqlAdd (table: string) (c: ColumnDef) =
    $"ALTER TABLE {table} ADD COLUMN {Table.columnDefSql c}"

  let dropSql (table: string) (columnName: string) =
    $"ALTER TABLE {table} DROP COLUMN {columnName}"

  let updateSql (views: CreateView list) (table: CreateTable) (left: ColumnDef) (right: ColumnDef) =
    if left.constraints <> right.constraints then
      Table.recreateSql views table |> Some
    else
      None

module Seed =
  let private formatValue (expr: Expr) =
    match expr with
    | String s -> $"'{s}'"
    | Integer i -> string i
    | Real r -> string r
    | Value v -> v

  let upsertSql (insert: InsertInto) =
    let cols = insert.columns |> sepComma id

    let valueRows =
      insert.values
      |> List.map (fun vals -> vals |> List.map formatValue |> String.concat ", " |> sprintf "(%s)")
      |> String.concat ", "

    $"INSERT OR REPLACE INTO {insert.table}({cols}) VALUES {valueRows}"
