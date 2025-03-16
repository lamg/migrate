module internal migrate.DeclarativeMigrations.SqlParser

open System
open System.Text.RegularExpressions
open System.IO
open Antlr4.Runtime
open SqliteParserCs
open FsToolkit.ErrorHandling
open Types

type Statement =
  | View of CreateView
  | Table of CreateTable
  | Index of CreateIndex
  | Trigger of CreateTrigger
  | StatList of Statement list

type SqlVisitor() =
  inherit SQLiteParserBaseVisitor<Statement>()

  let rec childrenToText (x: Tree.IParseTree) =
    seq {
      if x.ChildCount = 0 then

        yield x.GetText()
      else
        for n in { 0 .. x.ChildCount - 1 } do
          yield! n |> x.GetChild |> childrenToText
    }

  override this.VisitSql_stmt_list(context: SQLiteParser.Sql_stmt_listContext) =
    context.sql_stmt ()
    |> Array.choose (this.Visit >> Option.ofObj)
    |> Array.toList
    |> StatList

  override this.VisitCreate_table_stmt(context: SQLiteParser.Create_table_stmtContext) =
    let name = context.table_name().GetText().Trim '"'

    let columns =
      context.column_def ()
      |> Array.map (fun c ->
        let name = c.column_name().GetText().Trim '"'

        let colType =
          let t = c.type_name () |> Option.ofObj |> Option.map _.GetText().ToLower()

          match t with
          | Some "integer" -> SqlInteger
          | Some "text" -> SqlText
          | Some "real" -> SqlReal
          | Some "timestamp" -> SqlTimestamp
          | Some "string" -> SqlString
          | Some t -> failwith $"unexpected type {t}"
          | None -> SqlFlexible

        let getDefaultExpr (k: SQLiteParser.Column_constraintContext) =
          match k with
          | _ when k.signed_number () <> null -> Expr.Integer(k.signed_number().GetText() |> int)
          | _ when k.literal_value () <> null -> Expr.Value(k.literal_value().GetText())
          | _ when k.expr () <> null -> Expr.Value(k.expr().GetText())
          | _ -> failwith $"unexpected default expression for column constraint: {k.GetText()}"

        let getPrimaryKey (k: SQLiteParser.Column_constraintContext) =
          PrimaryKey
            { constraintName = k.name () |> Option.ofObj |> Option.map _.GetText()
              columns = []
              isAutoincrement = k.AUTOINCREMENT_() <> null }

        let getCheckExpr (k: SQLiteParser.Column_constraintContext) = k.expr () |> childrenToText

        let constraints =
          c.column_constraint ()
          |> Array.map (fun k ->
            [ k.NOT_() <> null && k.NULL_() <> null, lazy NotNull
              k.PRIMARY_() <> null && k.KEY_() <> null, lazy (getPrimaryKey k)
              k.UNIQUE_() <> null, lazy Unique []
              k.DEFAULT_() <> null, lazy Default(getDefaultExpr k)
              k.CHECK_() <> null, lazy Check(getCheckExpr k |> Seq.toList) ]
            |> List.choose (fun (cond, v) -> if cond then Some v.Value else None))
          |> Array.toList
          |> List.concat

        { name = name
          columnType = colType
          constraints = constraints })
      |> Array.toList

    let constraints =
      context.table_constraint ()
      |> Array.choose (fun c ->
        match c with
        | _ when c.PRIMARY_() <> null ->
          { constraintName = None
            columns = c.indexed_column () |> Array.map _.column_name().GetText() |> Array.toList
            isAutoincrement = false }
          |> PrimaryKey
          |> Some

        | _ when c.UNIQUE_() <> null ->
          c.indexed_column ()
          |> Array.map _.column_name().GetText()
          |> Array.toList
          |> Unique
          |> Some
        | _ when c.FOREIGN_() <> null ->
          let columns = c.column_name () |> Array.map _.GetText() |> Array.toList
          let refTable = c.foreign_key_clause().foreign_table().GetText()

          let refColumns =
            c.foreign_key_clause().column_name ()
            |> Array.map (fun x -> x.GetText())
            |> Array.toList

          Some(
            ForeignKey
              { columns = columns
                refTable = refTable
                refColumns = refColumns }
          )
        | _ -> None)
      |> Array.toList

    Table
      { name = name
        columns = columns
        constraints = constraints } //TODO parse table constraints


  override _.VisitCreate_view_stmt(context: SQLiteParser.Create_view_stmtContext) =
    let selectDependencies (context: SQLiteParser.Select_stmtContext) =
      context.select_core ()
      |> Seq.head
      |> _.table_or_subquery()
      |> Seq.map (fun t -> t.table_name().GetText())
      |> Seq.toList

    // FIXME it's possible the remaining UNION  clauses are ignored because of a bug in the grammar
    let sql = context.children |> Seq.map childrenToText |> Seq.concat

    let name = context.view_name().GetText().Trim '"'

    // FIXME assumes a query in the form `FROM table0, table1, …`
    let tables = context.select_stmt () |> selectDependencies

    let withTables, withDeps =
      match context.select_stmt().common_table_stmt () with
      | null -> [], []
      | ct ->
        ct.common_table_expression ()
        |> Array.map (fun x -> x.table_name().GetText(), selectDependencies (x.select_stmt ()))
        |> Array.unzip
        |> fun (xs, yss) -> Array.toList xs, yss |> Array.toList |> List.concat

    // FIXME for WITH statements inside WITH statements this trick might not work
    let topLevelTables = set tables + set withDeps - set withTables |> Set.toList

    View
      { name = name
        sqlTokens = sql
        dependencies = topLevelTables }

  override this.VisitCreate_index_stmt(context: SQLiteParser.Create_index_stmtContext) =
    let name = context.index_name().GetText().Trim '"'
    let table = context.table_name().GetText().Trim '"'

    let columns =
      context.indexed_column ()
      |> Array.map (fun i -> i.column_name().GetText())
      |> Array.toList

    Index
      { name = name
        table = table
        columns = columns }

  override this.VisitCreate_trigger_stmt(context: SQLiteParser.Create_trigger_stmtContext) =
    let sql = context.children |> Seq.map childrenToText |> Seq.concat
    let name = context.trigger_name().GetText().Trim '"'
    let table = context.table_name().GetText().Trim '"'

    Trigger
      { name = name
        sqlTokens = sql
        dependencies = [ table ] }

let prostProcViews (sql: string) (file: SqlFile) =
  let extractViewTokens view =
    let start = sql.IndexOf $"CREATE VIEW {view}"

    let viewStr =
      sql |> Seq.skip start |> Seq.takeWhile (fun c -> c <> ';') |> Array.ofSeq

    let r = new string (viewStr) |> _.Trim()
    [ r ]

  let views =
    file.views
    |> List.map (fun v ->
      { v with
          sqlTokens = v.name |> extractViewTokens })

  { file with views = views }

let parse (_file: string, sql: string) =
  use reader = new StringReader(sql)
  let input = AntlrInputStream reader
  let lexer = SQLiteLexer input
  let tokens = CommonTokenStream lexer
  let parser = SQLiteParser tokens
  parser.BuildParseTree <- true
  let ctx = parser.parse ()
  let visitor = SqlVisitor()

  option {
    let! first = ctx.children |> Seq.tryHead
    let! expr = visitor.Visit first |> Option.ofObj
    return expr
  }
  |> function
    | Some(StatList xs) ->
      xs
      |> List.fold
        (fun acc ->
          function
          | View v -> { acc with views = v :: acc.views }
          | Table t -> { acc with tables = t :: acc.tables }
          | Index i -> { acc with indexes = i :: acc.indexes }
          | Trigger t ->
            { acc with
                triggers = t :: acc.triggers }
          | StatList ys -> failwith $"unexpected statement list {ys}")
        emptyFile
      |> prostProcViews sql
      |> Ok
    | Some expr -> Error $"expecting statement list, got {expr}"
    | None -> Ok emptyFile
