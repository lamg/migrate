module Migrate.SqlParser

open SqlParser
open SqlParser.Ast

open Migrate.Types
open SqlParser.Dialects
open SqlParser.Tokens

let classifyStatement (initializedTables: string list) (acc: SqlFile) (s: Statement) =
  match box s with
  | :? Statement.Insert as s ->
    let cols = s.Columns |> Seq.map _.Value |> Seq.toList

    let vss =
      s.Source.Query.Body :?> SetExpression.ValuesExpression
      |> _.Values.Rows
      |> Seq.map (fun r ->
        r
        |> Seq.map (fun e ->
          match box e with
          | :? Expression.LiteralValue as l ->
            match box l.Value with
            | :? Value.SingleQuotedString as s -> s.Value |> String
            | :? Value.Number as n ->
              match n.AsInt() |> Option.ofNullable with
              | Some v -> v |> int |> Integer
              | None -> n.Value |> double |> Real
            | v -> failwith $"unsupported literal {v}"
          | :? Expression.UnaryOp as u when u.Op = UnaryOperator.Minus ->
            match u.Expression.AsLiteral().Value with
            | :? Value.Number as n ->
              match n.AsInt() |> Option.ofNullable with
              | Some v -> v |> int |> (fun x -> x * -1) |> Integer
              | None -> n.Value |> double |> (fun x -> x * -1.0) |> Real
            | v -> failwith $"unsupported literal {v}"
          | v -> failwith $"value {v} not supported in insert")
        |> Seq.toList)
      |> Seq.toList

    let ins =
      { table = s.Name.Values |> Seq.head |> _.Value
        columns = cols
        values = vss }

    if List.contains ins.table initializedTables then
      { acc with
          tableInits = ins :: acc.tableInits }
    else
      { acc with
          tableSyncs = ins :: acc.tableSyncs }
  | :? Statement.CreateTable as s ->
    let cols =
      s.Columns
      |> Seq.map (fun c ->
        let t =
          match box c.DataType with
          | :? DataType.Integer -> SqlInteger
          | :? DataType.Text -> SqlText
          | :? DataType.Real -> SqlReal
          | _ -> failwith $"unsupported type {c.DataType}"

        let cs =
          c.Options
          |> Seq.choose (fun c ->
            match box c.Option with
            | :? ColumnOption.Unique as u when u.IsPrimary -> PrimaryKey [] |> Some
            | :? ColumnOption.Unique -> Unique [] |> Some
            | :? ColumnOption.NotNull -> NotNull |> Some
            | :? ColumnOption.Default as d ->
              d.Expression.AsLiteral().Value.AsSingleQuoted().Value
              |> String
              |> Default
              |> Some
            | :? ColumnOption.DialectSpecific as d when d.Tokens.Contains(Word("AUTOINCREMENT")) ->
              Autoincrement |> Some
            | _ -> None)
          |> Seq.toList

        { name = c.Name.Value
          columnType = t
          constraints = cs })
      |> Seq.toList

    let empty = Sequence<TableConstraint>()

    let constraints =
      s.Constraints
      |> Option.ofObj
      |> Option.defaultValue empty
      |> Seq.choose (fun c ->

        match box c with
        | :? TableConstraint.Unique as d when d.IsPrimaryKey ->
          let cols = d.Columns |> Seq.map _.Value |> Seq.toList
          PrimaryKey cols |> Some
        | :? TableConstraint.Unique as d ->
          let cols = d.Columns |> Seq.map _.Value |> Seq.toList
          Unique cols |> Some
        | :? TableConstraint.ForeignKey as fk ->

          let fk =
            { columns = fk.Columns |> Seq.map (fun c -> c.Value) |> Seq.toList
              refTable = fk.ForeignTable.Values |> Seq.head |> _.Value
              refColumns = fk.ReferredColumns |> Seq.map _.Value |> Seq.toList }

          ForeignKey fk |> Some
        | _ -> None)
      |> Seq.toList

    let ct =
      { name = s.Name.Values |> Seq.head |> _.Value
        columns = cols
        constraints = constraints }

    { acc with tables = ct :: acc.tables }
  | :? Statement.CreateView as s ->
    let cv =
      { name = s.Name.Values |> Seq.head |> _.Value
        selectUnion = s.Query.Query.Body.AsSelect() }

    { acc with views = cv :: acc.views }
  | :? Statement.CreateIndex as s ->
    let name = s.Name.Values |> Seq.head |> _.Value
    let table = s.TableName.Values |> Seq.head |> _.Value

    let columns =
      s.Columns |> Seq.map (_.Expression.AsIdentifier().Ident.Value) |> Seq.toList

    let index =
      { name = name
        table = table
        columns = columns }

    { acc with
        indexes = index :: acc.indexes }
  | _ -> acc

let parseSql (inits: string list) (file: string) (sql: string) =
  try
    let ast = Parser().ParseSql(sql, SQLiteDialect())

    let emptyFile =
      { tables = []
        indexes = []
        tableSyncs = []
        tableInits = []
        views = [] }

    ast |> Seq.fold (classifyStatement inits) emptyFile |> Ok
  with :? ParserException as e ->
    Error $"Error parsing {file}({e.Line},{e.Column}): {e.Message}"
