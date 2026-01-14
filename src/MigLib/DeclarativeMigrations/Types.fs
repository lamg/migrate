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

type internal QueryByAnnotation = { columns: string list }

type internal CreateView =
  { name: string
    sqlTokens: string seq
    dependencies: string list
    queryByAnnotations: QueryByAnnotation list }

type internal CreateTable =
  { name: string
    columns: ColumnDef list
    constraints: ColumnConstraint list
    queryByAnnotations: QueryByAnnotation list }

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

/// Represents an extension table that extends a base table in a 1:1 relationship.
/// Extension tables follow the naming convention: {base_table}_{aspect}
/// and have their FK column as the PK (enforcing at most one extension per base record).
type internal ExtensionTable =
  {
    /// The extension table definition
    table: CreateTable
    /// The aspect name derived from the table suffix (e.g., "address" from "student_address")
    aspectName: string
    /// The FK column that references the base table (also the PK of this table)
    fkColumn: string
  }

/// Represents a base table with its detected extension tables.
/// Used for generating discriminated union types instead of option types.
type internal NormalizedTable =
  {
    /// The base table definition
    baseTable: CreateTable
    /// List of extension tables that extend this base table
    extensions: ExtensionTable list
  }

/// Represents validation errors for normalized schema detection.
type NormalizedSchemaError =
  /// Table or extension has nullable columns, which are not allowed in normalized schemas
  | NullableColumnsDetected of table: string * columns: string list
  /// Extension table has invalid foreign key relationship to base table
  | InvalidForeignKey of extension: string * expected: string * reason: string
  /// Extension table doesn't follow naming convention
  | InvalidNaming of table: string * expected: string
  /// Extension table FK column is not the PK (must be 1:1 relationship)
  | ForeignKeyNotPrimaryKey of extension: string * fkColumn: string
