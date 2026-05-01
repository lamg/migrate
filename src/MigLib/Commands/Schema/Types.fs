module MigLib.Commands.Schema.Types

type SqlType =
  | SqlInteger
  | SqlText
  | SqlReal
  | SqlTimestamp
  | SqlString

type EnumLikeDu =
  { typeName: string; cases: string list }

type Expr =
  | String of string
  | Integer of int
  | Real of double
  | Value of string

type InsertInto =
  { table: string
    columns: string list
    values: Expr list list }

type FkAction =
  | Cascade
  | Restrict
  | NoAction
  | SetNull
  | SetDefault

type ForeignKey =
  { columns: string list
    refTable: string
    refColumns: string list
    onDelete: FkAction option
    onUpdate: FkAction option }

type PrimaryKey =
  { constraintName: string option
    columns: string list
    isAutoincrement: bool }

type ColumnConstraint =
  | PrimaryKey of PrimaryKey
  | Autoincrement
  | NotNull
  | Unique of string list
  | Default of Expr
  | Check of string list
  | ForeignKey of ForeignKey

type ColumnDef =
  { name: string
    previousName: string option
    columnType: SqlType
    constraints: ColumnConstraint list
    enumLikeDu: EnumLikeDu option
    unitOfMeasure: string option }

type ViewColumn =
  { name: string
    columnType: SqlType
    enumLikeDu: EnumLikeDu option
    unitOfMeasure: string option }

type QueryByAnnotation = { columns: string list }

type QueryLikeAnnotation = { columns: string list }

type QueryByOrCreateAnnotation = { columns: string list }

type SelectOneAnnotation = SelectOneAnnotation

type InsertOrIgnoreAnnotation = InsertOrIgnoreAnnotation

type DeleteAllAnnotation = DeleteAllAnnotation

type UpsertAnnotation = UpsertAnnotation

type CreateView =
  { name: string
    previousName: string option
    sql: string
    declaredColumns: ViewColumn list
    dependencies: string list
    queryByAnnotations: QueryByAnnotation list
    queryLikeAnnotations: QueryLikeAnnotation list
    queryByOrCreateAnnotations: QueryByOrCreateAnnotation list
    selectOneAnnotations: SelectOneAnnotation list
    insertOrIgnoreAnnotations: InsertOrIgnoreAnnotation list
    deleteAllAnnotations: DeleteAllAnnotation list
    upsertAnnotations: UpsertAnnotation list }

type CreateTable =
  { name: string
    previousName: string option
    dropColumns: string list
    columns: ColumnDef list
    constraints: ColumnConstraint list
    queryByAnnotations: QueryByAnnotation list
    queryLikeAnnotations: QueryLikeAnnotation list
    queryByOrCreateAnnotations: QueryByOrCreateAnnotation list
    selectOneAnnotations: SelectOneAnnotation list
    insertOrIgnoreAnnotations: InsertOrIgnoreAnnotation list
    deleteAllAnnotations: DeleteAllAnnotation list
    upsertAnnotations: UpsertAnnotation list }

type CreateIndex =
  { name: string
    table: string
    columns: string list }

type CreateTrigger =
  { name: string
    sql: string
    dependencies: string list }

type SqlFile =
  { measureTypes: string list
    inserts: InsertInto list
    views: CreateView list
    tables: CreateTable list
    indexes: CreateIndex list
    triggers: CreateTrigger list }

type SchemaIdentity =
  { schemaHash: string
    schemaCommit: string option }

let emptyFile =
  { measureTypes = []
    inserts = []
    views = []
    tables = []
    indexes = []
    triggers = [] }

let foldResults
  (folder: 'state -> 'item -> Result<'state, string>)
  (initial: 'state)
  (items: 'item list)
  : Result<'state, string> =
  (Ok initial, items)
  ||> List.fold (fun state item ->
    match state with
    | Error _ -> state
    | Ok value -> folder value item)
