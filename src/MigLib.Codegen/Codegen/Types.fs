namespace MigLib.Codegen

/// Result of a successful `generate` run.
type CodegenResult =
  {
    /// Directory that received generated modules.
    outputDir: string
    namespaceName: string
    relationCount: int
    /// Absolute paths of files written this run.
    generatedFiles: string list
  }

module internal Types =
  [<RequireQualifiedAccess>]
  type RelationKind =
    | Table
    | View

  [<RequireQualifiedAccess>]
  type ColumnOverrideKind =
    | Bool
    | Int
    | UInt
    | Int64
    | DateTime

  type ColumnOverride =
    { column: string
      kind: ColumnOverrideKind }

  [<RequireQualifiedAccess>]
  type SortDirection =
    | Asc
    | Desc

  /// F# type for a select_with argument (from override or literal inference).
  [<RequireQualifiedAccess>]
  type SelectWithArgType =
    | Bool
    | Int
    | UInt
    | Int64
    | Float
    | String
    | DateTime

  type SelectWithArg =
    { name: string
      argType: SelectWithArgType }

  type SelectWithPlan =
    {
      args: SelectWithArg list
      /// View SELECT body with /*@name*/literal markers rewritten to @name.
      sql: string
    }

  [<RequireQualifiedAccess>]
  type Op =
    | Insert
    | InsertOrIgnore
    | InsertMany
    | Upsert
    | UpsertMany
    | SelectAll
    | SelectById
    | SelectBy of columns: string list
    | SelectOneBy of columns: string list
    /// SELECT by columns; INSERT insert-input and re-select when missing. Tables only.
    | SelectByOrInsert of columns: string list
    | SelectLike of column: string
    /// ORDER BY column DESC LIMIT n
    | SelectTop of column: string * limit: int
    /// ORDER BY column ASC LIMIT n
    | SelectBottom of column: string * limit: int
    /// ORDER BY listed columns; runtime skip/take (OFFSET/LIMIT)
    | SelectRange of orderBy: (string * SortDirection) list
    /// Parameterized view SELECT; names declared here, markers /*@name*/lit in view body.
    | SelectWith of args: string list
    | DeleteById
    | DeleteBy of columns: string list
    | DeleteAll

    member this.IsWrite =
      match this with
      | Insert
      | InsertOrIgnore
      | InsertMany
      | Upsert
      | UpsertMany
      | SelectByOrInsert _
      | DeleteById
      | DeleteBy _
      | DeleteAll -> true
      | SelectAll
      | SelectById
      | SelectBy _
      | SelectOneBy _
      | SelectLike _
      | SelectTop _
      | SelectBottom _
      | SelectRange _
      | SelectWith _ -> false

  type RelationAnnotation =
    {
      /// SQL relation name from CREATE TABLE/VIEW (may be set after association).
      sqlName: string option
      fsNameOverride: string option
      ops: Op list
      overrides: ColumnOverride list
      sourceFile: string
      sourceLine: int
      /// Full CREATE TABLE/VIEW statement text from the migration source (when captured).
      createSql: string option
    }

  type ColumnInfo =
    {
      name: string
      declaredType: string
      notNull: bool
      /// 1-based PK ordinal; 0 means not part of PK.
      pkOrdinal: int
      isAutoIncrement: bool
    }

  type RelationInfo =
    { name: string
      kind: RelationKind
      columns: ColumnInfo list }

    member this.PrimaryKeyColumns =
      this.columns
      |> List.filter (fun c -> c.pkOrdinal > 0)
      |> List.sortBy _.pkOrdinal

  type AnnotatedRelation =
    {
      sqlName: string
      fsName: string
      kind: RelationKind
      columns: ColumnInfo list
      ops: Op list
      overrides: Map<string, ColumnOverrideKind>
      /// Present when ops include SelectWith; rewritten SELECT + typed args.
      selectWith: SelectWithPlan option
    }

    member this.PrimaryKeyColumns =
      this.columns
      |> List.filter (fun c -> c.pkOrdinal > 0)
      |> List.sortBy _.pkOrdinal
