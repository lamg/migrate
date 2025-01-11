module migrate.DeclarativeMigrations.Types

type internal SqlType =
  | SqlInteger
  | SqlText
  | SqlReal
  | SqlTimestamp
  | SqlString
  | SqlFlexible

type internal Autoincrement = Autoincrement

type internal Expr =
  | String of string
  | Integer of int
  | Real of double
  | Value of string

type internal InsertInto =
  { table: string
    columns: string list
    values: Expr list list }

type internal ForeignKey =
  { columns: string list
    refTable: string
    refColumns: string list }

type internal PrimaryKey =
  { constraintName: string option
    columns: string list
    isAutoincrement: bool }

type internal ColumnConstraint =
  | PrimaryKey of PrimaryKey
  | Autoincrement
  | NotNull
  | Unique of string list
  | Default of Expr
  | Check of string list
  | ForeignKey of ForeignKey

type internal ColumnDef =
  { name: string
    columnType: SqlType
    constraints: ColumnConstraint list }

type internal CreateView =
  { name: string
    sqlTokens: string seq
    dependencies: string list }

type internal CreateTable =
  { name: string
    columns: ColumnDef list
    constraints: ColumnConstraint list }

type internal CreateIndex =
  { name: string
    table: string
    columns: string list }

type internal CreateTrigger =
  { name: string
    sqlTokens: string seq
    dependencies: string list }

type internal SqlFile =
  { inserts: InsertInto list
    views: CreateView list
    tables: CreateTable list
    indexes: CreateIndex list
    triggers: CreateTrigger list }

let internal emptyFile =
  { tables = []
    indexes = []
    inserts = []
    views = []
    triggers = [] }

type MigrationError =
  | ParsingFailed of message: string
  | MissingDependencies of left: string list * right: string list
  | ReadFileFailed of message: string
  | OpenDbFailed of message: string
  | ReadSchemaFailed of message: string
  | Composed of MigrationError list
  | FailedSteps of string list
