namespace MigLib.Codegen

module internal Emit =
  open System
  open System.Text
  open MigLib.Codegen.Naming
  open MigLib.Codegen.Types

  type private FsType =
    { typeName: string
      readExpr: string // function taking reader ordinal → value expr fragments
      readCall: int -> string
      boxExpr: string -> string }

  let private affinity (declaredType: string) =
    let t =
      if isNull declaredType then
        ""
      else
        declaredType.Trim().ToUpperInvariant()

    if t.Contains "INT" then
      "INTEGER"
    elif t.Contains "CHAR" || t.Contains "CLOB" || t.Contains "TEXT" then
      "TEXT"
    elif t.Contains "BLOB" || t = "" then
      "BLOB"
    elif t.Contains "REAL" || t.Contains "FLOA" || t.Contains "DOUB" then
      "REAL"
    else
      "TEXT"

  let private resolveFsType
    (col: ColumnInfo)
    (overrides: Map<string, ColumnOverrideKind>)
    : string * (int -> string) * (string -> string) =
    let ov = overrides |> Map.tryFind col.name

    let baseType, readFn, boxFn =
      match ov with
      | Some ColumnOverrideKind.Bool -> "bool", (fun i -> $"Query.readBool r {i}"), (fun e -> $"Query.boxBool ({e})")
      | Some ColumnOverrideKind.Int -> "int", (fun i -> $"Query.readInt r {i}"), (fun e -> $"box ({e})")
      | Some ColumnOverrideKind.UInt -> "uint32", (fun i -> $"Query.readUInt r {i}"), (fun e -> $"box (int64 ({e}))")
      | Some ColumnOverrideKind.DateTime ->
        "DateTimeOffset", (fun i -> $"Query.readDateTimeOffset r {i}"), (fun e -> $"Query.boxDateTimeOffset ({e})")
      | Some ColumnOverrideKind.Int64
      | None ->
        match affinity col.declaredType with
        | "INTEGER" -> "int64", (fun i -> $"Query.readInt64 r {i}"), (fun e -> $"box ({e})")
        | "REAL" -> "float", (fun i -> $"Query.readFloat r {i}"), (fun e -> $"box ({e})")
        | "BLOB" -> "byte[]", (fun i -> $"Query.readBlob r {i}"), (fun e -> $"box ({e})")
        | _ -> "string", (fun i -> $"Query.readString r {i}"), (fun e -> $"box ({e})")

    if col.notNull then
      baseType, readFn, boxFn
    else
      let optType = baseType + " option"

      let readOpt i =
        match ov with
        | Some ColumnOverrideKind.Bool -> $"Query.readBoolOption r {i}"
        | Some ColumnOverrideKind.Int -> $"Query.readIntOption r {i}"
        | Some ColumnOverrideKind.UInt -> $"Query.readUIntOption r {i}"
        | Some ColumnOverrideKind.DateTime -> $"Query.readDateTimeOffsetOption r {i}"
        | Some ColumnOverrideKind.Int64
        | None ->
          match affinity col.declaredType with
          | "INTEGER" -> $"Query.readInt64Option r {i}"
          | "REAL" -> $"Query.readFloatOption r {i}"
          | "BLOB" -> $"Query.readBlobOption r {i}"
          | _ -> $"Query.readStringOption r {i}"

      let boxOpt e =
        match ov with
        | Some ColumnOverrideKind.Bool -> $"Query.boxBoolOption ({e})"
        | Some ColumnOverrideKind.DateTime -> $"Query.boxDateTimeOffsetOption ({e})"
        | _ -> $"Query.boxOption ({e})"

      optType, readOpt, boxOpt

  let private indent (n: int) = String(' ', n)

  /// SQL as a normal F# string literal (identifiers use [brackets], so no quote escaping).
  let private sqlLit (sql: string) =
    "\"" + sql.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

  /// Multi-line SQL as a triple-quoted F# string when safe; otherwise fall back to sqlLit.
  let private multiLineSqlLit (sql: string) =
    if sql.Contains "\"\"\"" then
      sqlLit sql
    else
      "\"\"\"" + sql + "\"\"\""

  let private emitRecord (sb: StringBuilder) (typeName: string) (fields: (string * string) list) (indentLevel: int) =
    let ind = indent indentLevel
    sb.AppendLine $"{ind}type {typeName} =" |> ignore
    sb.AppendLine $"{ind}  {{" |> ignore

    for name, ty in fields do
      sb.AppendLine $"{ind}    {name}: {ty}" |> ignore

    sb.AppendLine $"{ind}  }}" |> ignore

  let private columnSelectList (rel: AnnotatedRelation) =
    rel.columns
    |> List.map (fun c -> Naming.quoteSqlIdent c.name)
    |> String.concat ", "

  let private emitMapRow
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (fieldMeta: (ColumnInfo * string * (int -> string) * (string -> string)) list)
    =
    sb.AppendLine $"let private mapRow (r: SqliteDataReader) : {rowType} ="
    |> ignore

    sb.AppendLine "  {" |> ignore

    fieldMeta
    |> List.iteri (fun i (col, fieldName, readFn, _) -> sb.AppendLine $"    {fieldName} = {readFn i}" |> ignore)

    sb.AppendLine "  }" |> ignore
    sb.AppendLine() |> ignore

  let private emitInsert
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (insertType: string)
    (insertCols: ColumnInfo list)
    (fieldMeta: Map<string, string * (string -> string)>)
    =
    let colSql =
      insertCols
      |> List.map (fun c -> Naming.quoteSqlIdent c.name)
      |> String.concat ", "

    let paramSql =
      insertCols |> List.map (fun c -> Naming.paramName c.name) |> String.concat ", "

    let sql =
      $"INSERT INTO {Naming.quoteSqlIdent rel.sqlName} ({colSql}) VALUES ({paramSql})"

    let hasAutoInc = rel.columns |> List.exists _.isAutoIncrement
    let returnType = if hasAutoInc then "int64" else "int"

    sb.AppendLine $"let insert (row: {insertType}) : TxnStep<{returnType}> ="
    |> ignore

    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for col in insertCols do
      let fieldName, boxFn = fieldMeta[col.name]
      let boxed = boxFn $"row.{fieldName}"
      let param = Naming.paramName col.name
      sb.AppendLine $"      \"{param}\", {boxed}" |> ignore

    sb.AppendLine "    ]" |> ignore

    if hasAutoInc then
      sb.AppendLine $"  TxnStep.bind (Query.exec {sqlLit sql} parameters) (fun _ -> Query.lastInsertRowId)"
      |> ignore
    else
      sb.AppendLine $"  Query.exec {sqlLit sql} parameters" |> ignore

    sb.AppendLine() |> ignore

  let private emitInsertOrIgnore
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (insertType: string)
    (insertCols: ColumnInfo list)
    (fieldMeta: Map<string, string * (string -> string)>)
    =
    let colSql =
      insertCols
      |> List.map (fun c -> Naming.quoteSqlIdent c.name)
      |> String.concat ", "

    let paramSql =
      insertCols |> List.map (fun c -> Naming.paramName c.name) |> String.concat ", "

    let sql =
      $"INSERT OR IGNORE INTO {Naming.quoteSqlIdent rel.sqlName} ({colSql}) VALUES ({paramSql})"

    sb.AppendLine $"let insertOrIgnore (row: {insertType}) : TxnStep<int> ="
    |> ignore

    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for col in insertCols do
      let fieldName, boxFn = fieldMeta[col.name]
      let boxed = boxFn $"row.{fieldName}"
      let param = Naming.paramName col.name
      sb.AppendLine $"      \"{param}\", {boxed}" |> ignore

    sb.AppendLine "    ]" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} parameters" |> ignore
    sb.AppendLine() |> ignore

  let private emitUpsert
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (allCols: ColumnInfo list)
    (fieldMeta: Map<string, string * (string -> string)>)
    =
    let pkCols = rel.PrimaryKeyColumns
    let nonPk = allCols |> List.filter (fun c -> c.pkOrdinal = 0)

    let colSql =
      allCols |> List.map (fun c -> Naming.quoteSqlIdent c.name) |> String.concat ", "

    let paramSql =
      allCols |> List.map (fun c -> Naming.paramName c.name) |> String.concat ", "

    let conflict =
      pkCols |> List.map (fun c -> Naming.quoteSqlIdent c.name) |> String.concat ", "

    let updates =
      if nonPk.IsEmpty then
        // SQLite needs something; no-op update on first pk
        let pk = pkCols.Head
        $"{Naming.quoteSqlIdent pk.name} = excluded.{Naming.quoteSqlIdent pk.name}"
      else
        nonPk
        |> List.map (fun c -> $"{Naming.quoteSqlIdent c.name} = excluded.{Naming.quoteSqlIdent c.name}")
        |> String.concat ", "

    let sql =
      $"INSERT INTO {Naming.quoteSqlIdent rel.sqlName} ({colSql}) VALUES ({paramSql}) ON CONFLICT({conflict}) DO UPDATE SET {updates}"

    sb.AppendLine $"let upsert (row: {rowType}) : TxnStep<int> =" |> ignore
    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for col in allCols do
      let fieldName, boxFn = fieldMeta[col.name]
      let boxed = boxFn $"row.{fieldName}"
      let param = Naming.paramName col.name
      sb.AppendLine $"      \"{param}\", {boxed}" |> ignore

    sb.AppendLine "    ]" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} parameters" |> ignore
    sb.AppendLine() |> ignore

  let private emitSelectAll (sb: StringBuilder) (rel: AnnotatedRelation) (rowType: string) =
    let sql = $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName}"
    sb.AppendLine $"let selectAll : TxnStep<{rowType} list> =" |> ignore
    sb.AppendLine $"  Query.queryList {sqlLit sql} [] mapRow" |> ignore
    sb.AppendLine() |> ignore

  /// Unfiltered row count: SELECT COUNT(*) FROM relation (tables and views).
  let private emitCount (sb: StringBuilder) (rel: AnnotatedRelation) =
    let sql = $"SELECT COUNT(*) FROM {Naming.quoteSqlIdent rel.sqlName}"
    sb.AppendLine "let count : TxnStep<int64> =" |> ignore
    sb.AppendLine $"  Query.scalar {sqlLit sql} []" |> ignore
    sb.AppendLine "  |> TxnStep.map (fun o -> Convert.ToInt64 o)" |> ignore
    sb.AppendLine() |> ignore

  let private filterParamName (filterName: string) =
    "@f_" + filterName.Replace('-', '_').ToLowerInvariant()

  /// Filter arg types are always non-null option wrappers (Some = apply predicate).
  let private filterFieldFsType (rel: AnnotatedRelation) (filter: FilterDef) : string * (string -> string) =
    let colName = filter.columns.Head

    let col =
      rel.columns
      |> List.find (fun c -> c.name.Equals(colName, StringComparison.OrdinalIgnoreCase))

    let notNullCol = { col with notNull = true }
    let ty, _, boxFn = resolveFsType notNullCol rel.overrides
    ty + " option", boxFn

  let private emitFilterSqlClause (filter: FilterDef) (param: string) : string =
    match filter.kind, filter.columns with
    | FilterKind.Eq, [ col ] -> $"{Naming.quoteSqlIdent col} = {param}"
    | FilterKind.Neq, [ col ] -> $"{Naming.quoteSqlIdent col} <> {param}"
    | FilterKind.Gt, [ col ] -> $"{Naming.quoteSqlIdent col} > {param}"
    | FilterKind.Gte, [ col ] -> $"{Naming.quoteSqlIdent col} >= {param}"
    | FilterKind.Lt, [ col ] -> $"{Naming.quoteSqlIdent col} < {param}"
    | FilterKind.Lte, [ col ] -> $"{Naming.quoteSqlIdent col} <= {param}"
    | FilterKind.LikePrefix, [ col ] -> $"{Naming.quoteSqlIdent col} LIKE {param}"
    | FilterKind.EqAny, cols ->
      cols
      |> List.map (fun c -> $"{Naming.quoteSqlIdent c} = {param}")
      |> String.concat " OR "
      |> fun body -> $"({body})"
    | _ -> failwith $"invalid filter shape for '{filter.name}'"

  /// Emit Filter record, emptyFilter, applyFilter, search, countByFilter when filter ops present.
  let private emitFilterSurface
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (orderBy: (string * SortDirection) list option)
    (emitSearch: bool)
    (emitCountByFilter: bool)
    =
    if rel.filters.IsEmpty then
      ()
    else
      let filterType = rowType + "Filter"

      let fieldMeta =
        rel.filters
        |> List.map (fun f ->
          let fieldName = sanitizeFsIdent (toPascalCase f.name)
          let fsType, boxFn = filterFieldFsType rel f
          f, fieldName, fsType, boxFn)

      sb.AppendLine $"type {filterType} =" |> ignore
      sb.AppendLine "  {" |> ignore

      for _, fieldName, fsType, _ in fieldMeta do
        sb.AppendLine $"    {fieldName}: {fsType}" |> ignore

      sb.AppendLine "  }" |> ignore
      sb.AppendLine() |> ignore

      sb.AppendLine $"let emptyFilter : {filterType} =" |> ignore
      sb.AppendLine "  {" |> ignore

      for _, fieldName, _, _ in fieldMeta do
        sb.AppendLine $"    {fieldName} = None" |> ignore

      sb.AppendLine "  }" |> ignore
      sb.AppendLine() |> ignore

      // Public: hand queries (stats, optional-column maps) reuse the same WHERE catalog.
      sb.AppendLine $"let applyFilter (f: {filterType}) : string * (string * obj) list ="
      |> ignore

      sb.AppendLine "  let clauses = ResizeArray<string>()" |> ignore
      sb.AppendLine "  let pars = ResizeArray<string * obj>()" |> ignore

      for filter, fieldName, _, boxFn in fieldMeta do
        let param = filterParamName filter.name
        let clause = emitFilterSqlClause filter param
        let clauseLit = clause.Replace("\"", "\\\"")
        let boxedV = boxFn "v"
        let boxedLike = boxFn "(v + \"%\")"
        sb.AppendLine $"  match f.{fieldName} with" |> ignore
        sb.AppendLine "  | None -> ()" |> ignore
        sb.AppendLine "  | Some v ->" |> ignore
        sb.AppendLine $"      clauses.Add \"{clauseLit}\"" |> ignore

        match filter.kind with
        | FilterKind.LikePrefix -> sb.AppendLine $"      pars.Add(\"{param}\", {boxedLike})" |> ignore
        | _ -> sb.AppendLine $"      pars.Add(\"{param}\", {boxedV})" |> ignore

      sb.AppendLine "  let where =" |> ignore
      sb.AppendLine "    if clauses.Count = 0 then \"1=1\"" |> ignore
      sb.AppendLine "    else String.Join(\" AND \", clauses)" |> ignore
      sb.AppendLine "  where, List.ofSeq pars" |> ignore
      sb.AppendLine() |> ignore

      if emitSearch then
        let orderBy = orderBy |> Option.defaultValue [ ("rowid", SortDirection.Asc) ]

        let orderSql =
          orderBy
          |> List.map (fun (col, dir) ->
            let order =
              match dir with
              | SortDirection.Desc -> "DESC"
              | SortDirection.Asc -> "ASC"

            $"{Naming.quoteSqlIdent col} {order}")
          |> String.concat ", "

        sb.AppendLine $"let search (filter: {filterType}) (skip: int) (take: int) : TxnStep<{rowType} list> ="
        |> ignore

        sb.AppendLine "  let where, filterPars = applyFilter filter" |> ignore
        sb.AppendLine "  let limit = max 0 take" |> ignore
        sb.AppendLine "  let offset = max 0 skip" |> ignore

        sb.AppendLine $"  let sql =" |> ignore

        sb.AppendLine
          $"    \"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} WHERE \" + where + \" ORDER BY {orderSql} LIMIT @take OFFSET @skip\""
        |> ignore

        sb.AppendLine "  let parameters = filterPars @ [ \"@skip\", box offset; \"@take\", box limit ]"
        |> ignore

        sb.AppendLine "  Query.queryList sql parameters mapRow" |> ignore
        sb.AppendLine() |> ignore

      if emitCountByFilter then
        sb.AppendLine $"let countByFilter (filter: {filterType}) : TxnStep<int64> ="
        |> ignore

        sb.AppendLine "  let where, filterPars = applyFilter filter" |> ignore

        sb.AppendLine $"  let sql = \"SELECT COUNT(*) FROM {Naming.quoteSqlIdent rel.sqlName} WHERE \" + where"
        |> ignore

        sb.AppendLine "  Query.scalar sql filterPars" |> ignore
        sb.AppendLine "  |> TxnStep.map (fun o -> Convert.ToInt64 o)" |> ignore
        sb.AppendLine() |> ignore

  let private emitSelectById
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (pk: ColumnInfo)
    (pkType: string)
    (boxFn: string -> string)
    =
    let sql =
      $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {Naming.quoteSqlIdent pk.name} = {Naming.paramName pk.name} LIMIT 1"

    let boxedId = boxFn "id"
    let pkParam = Naming.paramName pk.name

    sb.AppendLine $"let selectById (id: {pkType}) : TxnStep<{rowType} option> ="
    |> ignore

    sb.AppendLine $"  let parameters = [ \"{pkParam}\", {boxedId} ]" |> ignore
    sb.AppendLine $"  Query.queryOne {sqlLit sql} parameters mapRow" |> ignore
    sb.AppendLine() |> ignore

  let private emitSelectBy
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (cols: string list)
    (one: bool)
    (colTypes: Map<string, string * (string -> string)>)
    =
    let where =
      cols
      |> List.map (fun c -> $"{Naming.quoteSqlIdent c} = {Naming.paramName c}")
      |> String.concat " AND "

    let sql =
      let baseSql =
        $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {where}"

      if one then baseSql + " LIMIT 1" else baseSql

    let memberName =
      if one then
        selectOneByMemberName cols
      else
        selectByMemberName cols

    let paramList =
      cols
      |> List.map (fun c ->
        let ty, _ = colTypes[c]
        $"({toCamelCase c}: {ty})")
      |> String.concat " "

    let retType = if one then $"{rowType} option" else $"{rowType} list"

    sb.AppendLine $"let {memberName} {paramList} : TxnStep<{retType}> =" |> ignore
    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for c in cols do
      let _, boxFn = colTypes[c]

      sb.AppendLine $"      \"{Naming.paramName c}\", {boxFn (toCamelCase c)}"
      |> ignore

    sb.AppendLine "    ]" |> ignore

    if one then
      sb.AppendLine $"  Query.queryOne {sqlLit sql} parameters mapRow" |> ignore
    else
      sb.AppendLine $"  Query.queryList {sqlLit sql} parameters mapRow" |> ignore

    sb.AppendLine() |> ignore

  /// Select by equality columns; when missing, insert the provided row and re-select.
  /// Takes the insert input type (autoincrement columns omitted) and returns the full row.
  let private emitSelectByOrInsert
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (insertType: string)
    (insertCols: ColumnInfo list)
    (cols: string list)
    (fieldBoxMap: Map<string, string * (string -> string)>)
    =
    let where =
      cols
      |> List.map (fun c -> $"{Naming.quoteSqlIdent c} = {Naming.paramName c}")
      |> String.concat " AND "

    let selectSql =
      $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {where} LIMIT 1"

    let colSql =
      insertCols
      |> List.map (fun c -> Naming.quoteSqlIdent c.name)
      |> String.concat ", "

    let paramSql =
      insertCols |> List.map (fun c -> Naming.paramName c.name) |> String.concat ", "

    let insertSql =
      $"INSERT INTO {Naming.quoteSqlIdent rel.sqlName} ({colSql}) VALUES ({paramSql})"

    let memberName = selectByOrInsertMemberName cols

    sb.AppendLine $"let {memberName} (row: {insertType}) : TxnStep<{rowType}> ="
    |> ignore

    sb.AppendLine "  txn {" |> ignore
    sb.AppendLine "    let selectParameters =" |> ignore
    sb.AppendLine "      [" |> ignore

    for c in cols do
      let fieldName, boxFn = fieldBoxMap[c]
      let boxed = boxFn $"row.{fieldName}"
      sb.AppendLine $"        \"{Naming.paramName c}\", {boxed}" |> ignore

    sb.AppendLine "      ]" |> ignore

    sb.AppendLine $"    match! Query.queryOne {sqlLit selectSql} selectParameters mapRow with"
    |> ignore

    sb.AppendLine "    | Some existing -> return existing" |> ignore
    sb.AppendLine "    | None ->" |> ignore
    sb.AppendLine "      let insertParameters =" |> ignore
    sb.AppendLine "        [" |> ignore

    for col in insertCols do
      let fieldName, boxFn = fieldBoxMap[col.name]
      let boxed = boxFn $"row.{fieldName}"
      sb.AppendLine $"          \"{Naming.paramName col.name}\", {boxed}" |> ignore

    sb.AppendLine "        ]" |> ignore

    sb.AppendLine $"      do! Query.exec {sqlLit insertSql} insertParameters |> TxnStep.map ignore"
    |> ignore

    sb.AppendLine $"      match! Query.queryOne {sqlLit selectSql} selectParameters mapRow with"
    |> ignore

    sb.AppendLine "      | Some created -> return created" |> ignore

    sb.AppendLine "      | None -> return! TxnStep.fail \"select_by_or_insert failed to load inserted row\""
    |> ignore

    sb.AppendLine "  }" |> ignore
    sb.AppendLine() |> ignore

  let private emitSelectLike (sb: StringBuilder) (rel: AnnotatedRelation) (rowType: string) (column: string) =
    let sql =
      $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {Naming.quoteSqlIdent column} LIKE {Naming.paramName column}"

    let memberName = selectLikeMemberName column

    sb.AppendLine $"let {memberName} (pattern: string) : TxnStep<{rowType} list> ="
    |> ignore

    sb.AppendLine $"  let parameters = [ \"{Naming.paramName column}\", box pattern ]"
    |> ignore

    sb.AppendLine $"  Query.queryList {sqlLit sql} parameters mapRow" |> ignore
    sb.AppendLine() |> ignore

  let private emitSelectTopOrBottom
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (column: string)
    (limit: int)
    (descending: bool)
    =
    let order = if descending then "DESC" else "ASC"

    let sql =
      $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} ORDER BY {Naming.quoteSqlIdent column} {order} LIMIT {limit}"

    let memberName =
      if descending then
        selectTopMemberName column limit
      else
        selectBottomMemberName column limit

    sb.AppendLine $"let {memberName} : TxnStep<{rowType} list> =" |> ignore
    sb.AppendLine $"  Query.queryList {sqlLit sql} [] mapRow" |> ignore
    sb.AppendLine() |> ignore

  let private emitSelectRange
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (rowType: string)
    (orderBy: (string * SortDirection) list)
    =
    let orderSql =
      orderBy
      |> List.map (fun (col, dir) ->
        let order =
          match dir with
          | SortDirection.Desc -> "DESC"
          | SortDirection.Asc -> "ASC"

        $"{Naming.quoteSqlIdent col} {order}")
      |> String.concat ", "

    let sql =
      $"SELECT {columnSelectList rel} FROM {Naming.quoteSqlIdent rel.sqlName} ORDER BY {orderSql} LIMIT @limit OFFSET @offset"

    let memberName = selectRangeMemberName orderBy

    sb.AppendLine $"let {memberName} (skip: int) (take: int) : TxnStep<{rowType} list> ="
    |> ignore

    sb.AppendLine "  let limit = max 0 take" |> ignore

    sb.AppendLine "  let parameters = [ \"@offset\", box skip; \"@limit\", box limit ]"
    |> ignore

    sb.AppendLine $"  Query.queryList {sqlLit sql} parameters mapRow" |> ignore
    sb.AppendLine() |> ignore

  let private selectWithArgFs (argType: SelectWithArgType) : string * (string -> string) =
    match argType with
    | SelectWithArgType.Bool -> "bool", fun e -> $"Query.boxBool ({e})"
    | SelectWithArgType.Int -> "int", fun e -> $"box ({e})"
    | SelectWithArgType.UInt -> "uint32", fun e -> $"box (int64 ({e}))"
    | SelectWithArgType.Int64 -> "int64", fun e -> $"box ({e})"
    | SelectWithArgType.Float -> "float", fun e -> $"box ({e})"
    | SelectWithArgType.String -> "string", fun e -> $"box ({e})"
    | SelectWithArgType.DateTime -> "DateTimeOffset", fun e -> $"Query.boxDateTimeOffset ({e})"

  let private emitSelectWith (sb: StringBuilder) (rel: AnnotatedRelation) (rowType: string) (plan: SelectWithPlan) =
    let paramList =
      plan.args
      |> List.map (fun a ->
        let ty, _ = selectWithArgFs a.argType
        $"({toCamelCase a.name}: {ty})")
      |> String.concat " "

    sb.AppendLine $"let selectWith {paramList} : TxnStep<{rowType} list> ="
    |> ignore

    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for a in plan.args do
      let _, boxFn = selectWithArgFs a.argType
      let param = Naming.paramName a.name
      let camel = toCamelCase a.name
      sb.AppendLine $"      \"{param}\", {boxFn camel}" |> ignore

    sb.AppendLine "    ]" |> ignore
    sb.AppendLine $"  Query.queryList {sqlLit plan.sql} parameters mapRow" |> ignore
    sb.AppendLine() |> ignore

  let private emitDeleteById
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (pk: ColumnInfo)
    (pkType: string)
    (boxFn: string -> string)
    =
    let sql =
      $"DELETE FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {Naming.quoteSqlIdent pk.name} = {Naming.paramName pk.name}"

    let boxedId = boxFn "id"
    let pkParam = Naming.paramName pk.name
    sb.AppendLine $"let deleteById (id: {pkType}) : TxnStep<int> =" |> ignore
    sb.AppendLine $"  let parameters = [ \"{pkParam}\", {boxedId} ]" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} parameters" |> ignore
    sb.AppendLine() |> ignore

  let private emitDeleteBy
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (cols: string list)
    (colTypes: Map<string, string * (string -> string)>)
    =
    let where =
      cols
      |> List.map (fun c -> $"{Naming.quoteSqlIdent c} = {Naming.paramName c}")
      |> String.concat " AND "

    let sql = $"DELETE FROM {Naming.quoteSqlIdent rel.sqlName} WHERE {where}"
    let memberName = deleteByMemberName cols

    let paramList =
      cols
      |> List.map (fun c ->
        let ty, _ = colTypes[c]
        $"({toCamelCase c}: {ty})")
      |> String.concat " "

    sb.AppendLine $"let {memberName} {paramList} : TxnStep<int> =" |> ignore
    sb.AppendLine "  let parameters =" |> ignore
    sb.AppendLine "    [" |> ignore

    for c in cols do
      let _, boxFn = colTypes[c]

      sb.AppendLine $"      \"{Naming.paramName c}\", {boxFn (toCamelCase c)}"
      |> ignore

    sb.AppendLine "    ]" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} parameters" |> ignore
    sb.AppendLine() |> ignore

  let private emitDeleteAll (sb: StringBuilder) (rel: AnnotatedRelation) =
    let sql = $"DELETE FROM {Naming.quoteSqlIdent rel.sqlName}"
    sb.AppendLine "let deleteAll : TxnStep<int> =" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} []" |> ignore
    sb.AppendLine() |> ignore

  let private emitDeleteMatching
    (sb: StringBuilder)
    (rel: AnnotatedRelation)
    (targetTable: string)
    (keyColumn: string)
    =
    let sql =
      $"DELETE FROM {Naming.quoteSqlIdent targetTable} WHERE {Naming.quoteSqlIdent keyColumn} IN (SELECT {Naming.quoteSqlIdent keyColumn} FROM {Naming.quoteSqlIdent rel.sqlName})"

    sb.AppendLine "let deleteMatching : TxnStep<int> =" |> ignore
    sb.AppendLine $"  Query.exec {sqlLit sql} []" |> ignore
    sb.AppendLine() |> ignore

  let private emitMany (sb: StringBuilder) (fnName: string) (elemType: string) (singleFn: string) =
    sb.AppendLine $"let {fnName} (rows: {elemType} seq) : TxnStep<unit> =" |> ignore
    sb.AppendLine "  txn {" |> ignore
    sb.AppendLine "    for row in rows do" |> ignore
    sb.AppendLine $"      do! {singleFn} row |> TxnStep.map ignore" |> ignore
    sb.AppendLine "  }" |> ignore
    sb.AppendLine() |> ignore

  let private emitRelationBody (sb: StringBuilder) (rel: AnnotatedRelation) =
    let rowType = rel.fsName

    let fieldMetaList =
      rel.columns
      |> List.map (fun col ->
        let fieldName = sanitizeFsIdent (toPascalCase col.name)
        let ty, readFn, boxFn = resolveFsType col rel.overrides
        col, fieldName, ty, readFn, boxFn)

    let fields =
      fieldMetaList |> List.map (fun (_, fieldName, ty, _, _) -> fieldName, ty)

    let fieldBoxMap =
      fieldMetaList
      |> List.map (fun (col, fieldName, _, _, boxFn) -> col.name, (fieldName, boxFn))
      |> Map.ofList

    let colTypeMap =
      fieldMetaList
      |> List.map (fun (col, _, ty, _, boxFn) -> col.name, (ty, boxFn))
      |> Map.ofList

    emitRecord sb rowType fields 0
    sb.AppendLine() |> ignore

    let needsInsertType =
      rel.ops
      |> List.exists (function
        | Op.Insert
        | Op.InsertOrIgnore
        | Op.InsertMany
        | Op.SelectByOrInsert _ -> true
        | _ -> false)

    let insertCols = rel.columns |> List.filter (fun c -> not c.isAutoIncrement)

    let insertTypeName =
      if needsInsertType then
        let insertFields =
          fieldMetaList
          |> List.filter (fun (col, _, _, _, _) -> not col.isAutoIncrement)
          |> List.map (fun (_, fieldName, ty, _, _) -> fieldName, ty)

        let name =
          if insertCols.Length = rel.columns.Length then
            rowType
          else
            rowType + "Insert"

        if name <> rowType then
          emitRecord sb name insertFields 0
          sb.AppendLine() |> ignore

        name
      else
        rowType

    let mapMeta =
      fieldMetaList
      |> List.map (fun (col, fieldName, _, readFn, boxFn) -> col, fieldName, readFn, boxFn)

    emitMapRow sb rel rowType mapMeta

    let filterSearchOrder =
      rel.ops
      |> List.tryPick (function
        | Op.FilterSearch orderBy -> Some orderBy
        | _ -> None)

    let emitFilterSearch = filterSearchOrder.IsSome
    let emitFilterCount = rel.ops |> List.contains Op.FilterCount

    if not rel.filters.IsEmpty && (emitFilterSearch || emitFilterCount) then
      emitFilterSurface sb rel rowType filterSearchOrder emitFilterSearch emitFilterCount

    // Emit single-row ops first, then bulk wrappers that call them
    let orderedOps =
      rel.ops
      |> List.filter (function
        | Op.InsertMany
        | Op.UpsertMany
        | Op.FilterSearch _
        | Op.FilterCount -> false
        | _ -> true)

    for op in orderedOps do
      match op with
      | Op.Insert -> emitInsert sb rel insertTypeName insertCols fieldBoxMap
      | Op.InsertOrIgnore -> emitInsertOrIgnore sb rel insertTypeName insertCols fieldBoxMap
      | Op.Upsert -> emitUpsert sb rel rowType rel.columns fieldBoxMap
      | Op.SelectAll -> emitSelectAll sb rel rowType
      | Op.Count -> emitCount sb rel
      | Op.SelectById ->
        match rel.PrimaryKeyColumns with
        | [ pk ] ->
          let ty, boxFn = colTypeMap[pk.name]
          emitSelectById sb rel rowType pk ty boxFn
        | _ -> ()
      | Op.SelectBy cols -> emitSelectBy sb rel rowType cols false colTypeMap
      | Op.SelectOneBy cols -> emitSelectBy sb rel rowType cols true colTypeMap
      | Op.SelectByOrInsert cols -> emitSelectByOrInsert sb rel rowType insertTypeName insertCols cols fieldBoxMap
      | Op.SelectLike col -> emitSelectLike sb rel rowType col
      | Op.SelectTop(col, limit) -> emitSelectTopOrBottom sb rel rowType col limit true
      | Op.SelectBottom(col, limit) -> emitSelectTopOrBottom sb rel rowType col limit false
      | Op.SelectRange orderBy -> emitSelectRange sb rel rowType orderBy
      | Op.SelectWith _ ->
        match rel.selectWith with
        | Some plan -> emitSelectWith sb rel rowType plan
        | None -> ()
      | Op.DeleteById ->
        match rel.PrimaryKeyColumns with
        | [ pk ] ->
          let ty, boxFn = colTypeMap[pk.name]
          emitDeleteById sb rel pk ty boxFn
        | _ -> ()
      | Op.DeleteBy cols -> emitDeleteBy sb rel cols colTypeMap
      | Op.DeleteAll -> emitDeleteAll sb rel
      | Op.DeleteMatching(targetTable, keyColumn) -> emitDeleteMatching sb rel targetTable keyColumn
      | Op.InsertMany
      | Op.UpsertMany
      | Op.FilterSearch _
      | Op.FilterCount -> ()

    if rel.ops |> List.contains Op.InsertMany then
      let single =
        if
          rel.ops |> List.contains Op.InsertOrIgnore
          && not (rel.ops |> List.contains Op.Insert)
        then
          "insertOrIgnore"
        else
          "insert"

      emitMany sb "insertMany" insertTypeName single

    if rel.ops |> List.contains Op.UpsertMany then
      emitMany sb "upsertMany" rowType "upsert"

  /// Emit one complete source file for a relation module under namespaceName.
  let emitRelationFile (namespaceName: string) (rel: AnnotatedRelation) : string =
    let sb = StringBuilder()
    let moduleName = $"{namespaceName}.{rel.fsName}"
    sb.AppendLine "// <auto-generated />" |> ignore
    sb.AppendLine "// Generated by mig codegen. Do not edit by hand." |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine $"module {moduleName}" |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine "open System" |> ignore
    sb.AppendLine "open Microsoft.Data.Sqlite" |> ignore
    sb.AppendLine "open MigLib" |> ignore
    sb.AppendLine "open MigLib.Query" |> ignore
    sb.AppendLine "open MigLib.Runtime.TxnStep" |> ignore
    sb.AppendLine() |> ignore
    emitRelationBody sb rel
    sb.ToString()

  /// Reserved module/file name for generated <c>{namespace}.Migration.migrate</c>.
  let migrationModuleName = "Migration"

  /// Emit Stores/Migration.fs with <c>let migrate dbPath</c>.
  let emitMigrationFile (namespaceName: string) (expectedSchema: string) (migrationSql: string) : string =
    let sb = StringBuilder()
    let moduleName = $"{namespaceName}.{migrationModuleName}"
    sb.AppendLine "// <auto-generated />" |> ignore
    sb.AppendLine "// Generated by mig codegen. Do not edit by hand." |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine $"module {moduleName}" |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine "open System.Threading.Tasks" |> ignore
    sb.AppendLine "open MigLib" |> ignore
    sb.AppendLine() |> ignore

    sb.AppendLine("let private expectedSchema = " + multiLineSqlLit expectedSchema)
    |> ignore

    sb.AppendLine() |> ignore

    sb.AppendLine("let private migrationSql = " + multiLineSqlLit migrationSql)
    |> ignore

    sb.AppendLine() |> ignore

    sb.AppendLine "/// Apply the compiled snapshot (empty DB) or hop + catalog check."
    |> ignore

    sb.AppendLine "let migrate (dbPath: string) : Task<Result<DbTxnBuilder, MigError>> ="
    |> ignore

    sb.AppendLine "  Migrate.migrate dbPath expectedSchema migrationSql" |> ignore

    sb.ToString()

  let autoGeneratedMarker = "// <auto-generated />"
