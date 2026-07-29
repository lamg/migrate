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
    | SelectLike of column: string
    /// ORDER BY column DESC LIMIT n
    | SelectTop of column: string * limit: int
    /// ORDER BY column ASC LIMIT n
    | SelectBottom of column: string * limit: int
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
      | DeleteById
      | DeleteBy _
      | DeleteAll -> true
      | SelectAll
      | SelectById
      | SelectBy _
      | SelectOneBy _
      | SelectLike _
      | SelectTop _
      | SelectBottom _ -> false

  type RelationAnnotation =
    {
      /// SQL relation name from CREATE TABLE/VIEW (may be set after association).
      sqlName: string option
      fsNameOverride: string option
      ops: Op list
      overrides: ColumnOverride list
      sourceFile: string
      sourceLine: int
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
    { sqlName: string
      fsName: string
      kind: RelationKind
      columns: ColumnInfo list
      ops: Op list
      overrides: Map<string, ColumnOverrideKind> }

    member this.PrimaryKeyColumns =
      this.columns
      |> List.filter (fun c -> c.pkOrdinal > 0)
      |> List.sortBy _.pkOrdinal
