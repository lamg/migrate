module MigLib.SchemaScript

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Reflection
open FsToolkit.ErrorHandling
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open Microsoft.FSharp.Reflection
open MigLib.Db
open MigLib.DeclarativeMigrations.Types
open MigLib.SchemaReflection

let private isRecordType (t: Type) =
  try
    FSharpType.IsRecord(t, true)
  with _ ->
    false

let private isUnionType (t: Type) =
  try
    FSharpType.IsUnion(t, true)
  with _ ->
    false

let private getTypeAttributes<'a when 'a :> Attribute> (t: Type) : 'a list =
  t.GetCustomAttributes(typeof<'a>, true) |> Seq.cast<'a> |> Seq.toList

let private isViewType (t: Type) =
  not (getTypeAttributes<ViewAttribute> t).IsEmpty
  || not (getTypeAttributes<ViewSqlAttribute> t).IsEmpty

let private createFsiSession () =
  let inReader = new StringReader ""
  let outWriter = new StringWriter()
  let errorWriter = new StringWriter()

  let argv = [| "fsi.exe"; "--noninteractive"; "--nologo"; "--quiet" |]

  let config = FsiEvaluationSession.GetDefaultConfiguration()

  let session =
    FsiEvaluationSession.Create(config, argv, inReader, outWriter, errorWriter)

  session, outWriter, errorWriter

let private formatDiagnostic (diagnostic: FSharpDiagnostic) : string =
  let fileName =
    if String.IsNullOrWhiteSpace diagnostic.FileName then
      "<unknown>"
    else
      diagnostic.FileName

  let severity =
    match diagnostic.Severity with
    | FSharpDiagnosticSeverity.Error -> "error"
    | FSharpDiagnosticSeverity.Warning -> "warning"
    | _ -> "info"

  $"{fileName}:{diagnostic.StartLine}:{diagnostic.StartColumn} {severity} FS{diagnostic.ErrorNumber}: {diagnostic.Message}"

let private getScriptDefinedTypes (session: FsiEvaluationSession) : Type list =
  session.DynamicAssemblies
  |> Array.collect (fun assembly -> assembly.GetTypes())
  |> Array.filter (fun t -> not t.IsGenericTypeDefinition)
  |> Array.filter (fun t -> not (t.Name.StartsWith "<"))
  |> Array.filter (fun t -> isRecordType t || isUnionType t)
  |> Array.toList
  |> List.distinctBy (fun t -> t.AssemblyQualifiedName)

let private isScriptModuleType (t: Type) =
  t.IsClass
  && t.IsAbstract
  && t.IsSealed
  && not (String.IsNullOrWhiteSpace t.FullName)
  && not (t.FullName.StartsWith "<")
  && t.FullName.Contains "FSI_"

let private readStaticModuleValues (moduleType: Type) : (string * obj) list =
  let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

  let propertyValues =
    moduleType.GetProperties flags
    |> Array.filter (fun property ->
      property.GetIndexParameters().Length = 0
      && not (isNull property.GetMethod)
      && property.GetMethod.IsStatic)
    |> Array.choose (fun property ->
      try
        Some(property.Name, property.GetValue null)
      with _ ->
        None)
    |> Array.toList

  let propertyNames = propertyValues |> List.map fst |> Set.ofList

  let fieldValues =
    moduleType.GetFields flags
    |> Array.filter (fun field ->
      field.IsStatic
      && not field.IsSpecialName
      && not (field.Name.EndsWith "@")
      && not (propertyNames.Contains field.Name))
    |> Array.choose (fun field ->
      try
        Some(field.Name, field.GetValue null)
      with _ ->
        None)
    |> Array.toList

  propertyValues @ fieldValues

let private getScriptBoundValues (session: FsiEvaluationSession) : (string * obj) list =
  session.DynamicAssemblies
  |> Array.collect (fun assembly ->
    assembly.GetTypes()
    |> Array.filter isScriptModuleType
    |> Array.collect (fun moduleType -> readStaticModuleValues moduleType |> List.toArray))
  |> Array.filter (fun (_, value) -> not (isNull value))
  |> Array.toList

let private evaluateScript (scriptPath: string) : Result<Type list * (string * obj) list, string> =
  result {
    if not (File.Exists scriptPath) then
      return! Error $"Schema script was not found: {scriptPath}"

    let session, outWriter, errorWriter = createFsiSession ()
    use _session = session
    use _outWriter = outWriter
    use _errorWriter = errorWriter

    let status, diagnostics = session.EvalScriptNonThrowing scriptPath

    let errorDiagnostics =
      diagnostics
      |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

    if not (Array.isEmpty errorDiagnostics) then
      let messages = errorDiagnostics |> Array.map formatDiagnostic |> String.concat "\n"
      return! Error $"Failed to evaluate script '{scriptPath}':\n{messages}"

    match status with
    | Choice1Of2() -> ()
    | Choice2Of2 ex ->
      let output = outWriter.ToString().Trim()
      let errors = errorWriter.ToString().Trim()

      let extra =
        [ if not (String.IsNullOrWhiteSpace output) then
            yield $"stdout:\n{output}"

          if not (String.IsNullOrWhiteSpace errors) then
            yield $"stderr:\n{errors}" ]
        |> String.concat "\n"

      if String.IsNullOrWhiteSpace extra then
        return! Error $"Failed to evaluate script '{scriptPath}': {ex.Message}"
      else
        return! Error $"Failed to evaluate script '{scriptPath}': {ex.Message}\n{extra}"

    let reflectedTypes = getScriptDefinedTypes session

    let boundValues = getScriptBoundValues session

    return reflectedTypes, boundValues
  }

let private toExpr (value: obj) : Result<Expr, string> =
  if isNull value then
    Error "Null seed values are not supported"
  else
    match value with
    | :? string as v -> Ok(String v)
    | :? int8 as v -> Ok(Integer(int v))
    | :? int16 as v -> Ok(Integer(int v))
    | :? int as v -> Ok(Integer v)
    | :? int64 as v -> Ok(Value(v.ToString(CultureInfo.InvariantCulture)))
    | :? uint8 as v -> Ok(Integer(int v))
    | :? uint16 as v -> Ok(Value(v.ToString(CultureInfo.InvariantCulture)))
    | :? uint32 as v -> Ok(Value(v.ToString(CultureInfo.InvariantCulture)))
    | :? uint64 as v -> Ok(Value(v.ToString(CultureInfo.InvariantCulture)))
    | :? float32 as v -> Ok(Real(float v))
    | :? float as v -> Ok(Real v)
    | :? decimal as v -> Ok(Value(v.ToString(CultureInfo.InvariantCulture)))
    | :? bool as v -> Ok(Integer(if v then 1 else 0))
    | :? DateTime as v -> Ok(String(v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
    | :? (byte[]) as v ->
      let hex =
        v
        |> Seq.map (fun byteValue -> byteValue.ToString("X2", CultureInfo.InvariantCulture))
        |> String.concat ""

      Ok(Value $"X'{hex}'")
    | _ -> Error $"Unsupported seed value type '{value.GetType().FullName}'"

let private tryGetPrimaryKeyColumn (table: CreateTable) : ColumnDef option =
  table.columns
  |> List.tryFind (fun column ->
    column.constraints
    |> List.exists (function
      | PrimaryKey _ -> true
      | _ -> false))

let private resolvePrimaryKeyValue
  (referencedType: Type)
  (referencedValue: obj)
  (referencedTable: CreateTable)
  : Result<Expr, string> =
  result {
    let! primaryKeyColumn =
      match tryGetPrimaryKeyColumn referencedTable with
      | Some column -> Ok column
      | None -> Error $"Referenced table '{referencedTable.name}' has no primary key"

    let fields = FSharpType.GetRecordFields(referencedType, true)

    let field =
      fields
      |> Array.tryFind (fun property ->
        String.Equals(toSnakeCase property.Name, primaryKeyColumn.name, StringComparison.OrdinalIgnoreCase))

    let! primaryKeyField =
      match field with
      | Some property -> Ok property
      | None ->
        let available = fields |> Array.map _.Name |> String.concat ", "

        Error
          $"Unable to find primary-key field '{primaryKeyColumn.name}' on type '{referencedType.Name}'. Available fields: {available}"

    let primaryKeyValue = primaryKeyField.GetValue referencedValue

    return! toExpr primaryKeyValue
  }

let private createSeedRow
  (tableByType: Dictionary<Type, CreateTable>)
  (recordType: Type)
  (recordValue: obj)
  : Result<string * Expr list, string> =
  result {
    let! table =
      match tableByType.TryGetValue recordType with
      | true, table -> Ok table
      | false, _ -> Error $"Type '{recordType.Name}' does not map to a table"

    let fields = FSharpType.GetRecordFields(recordType, true)
    let values = FSharpValue.GetRecordFields(recordValue, true)

    let valuesByColumn = Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase)

    for index in 0 .. fields.Length - 1 do
      let field = fields.[index]
      let fieldValue = values.[index]

      if
        field.PropertyType = typeof<int64>
        || field.PropertyType = typeof<string>
        || field.PropertyType = typeof<float>
        || field.PropertyType = typeof<byte[]>
      then
        let columnName = toSnakeCase field.Name
        let! expr = toExpr fieldValue
        valuesByColumn[columnName] <- expr
      elif tableByType.ContainsKey field.PropertyType then
        let fkColumnName = $"{toSnakeCase field.Name}_id"

        let! referencedTable =
          match tableByType.TryGetValue field.PropertyType with
          | true, candidate -> Ok candidate
          | false, _ -> Error $"Referenced type '{field.PropertyType.Name}' was not mapped to a table"

        if isNull fieldValue then
          return! Error $"Seed field '{recordType.Name}.{field.Name}' cannot be null"

        let! fkExpr = resolvePrimaryKeyValue field.PropertyType fieldValue referencedTable

        valuesByColumn[fkColumnName] <- fkExpr
      else
        return! Error $"Unsupported seed field type '{recordType.Name}.{field.Name}: {field.PropertyType.Name}'"

    let! rowValues =
      table.columns
      |> foldResults
        (fun expressions column ->
          match valuesByColumn.TryGetValue column.name with
          | true, value -> Ok(expressions @ [ value ])
          | false, _ ->
            Error
              $"Seed value for type '{recordType.Name}' does not provide column '{column.name}' required by table '{table.name}'")
        []

    return table.name, rowValues
  }

let private toSeedRecordObjects
  (tableByType: Dictionary<Type, CreateTable>)
  (boundValues: (string * obj) list)
  : obj list =
  boundValues
  |> List.collect (fun (name, value) ->
    if String.Equals(name, "it", StringComparison.Ordinal) then
      []
    elif isNull value then
      []
    else
      let valueType = value.GetType()

      if tableByType.ContainsKey valueType then
        [ value ]
      elif typeof<IEnumerable>.IsAssignableFrom valueType && valueType <> typeof<string> then
        (value :?> IEnumerable)
        |> Seq.cast<obj>
        |> Seq.filter (fun item -> not (isNull item))
        |> Seq.filter (fun item -> tableByType.ContainsKey(item.GetType()))
        |> Seq.toList
      else
        [])

let private tableDependencies (tableByName: Map<string, CreateTable>) (tableName: string) : Set<string> =
  let table = tableByName[tableName]

  table.columns
  |> List.collect (fun column ->
    column.constraints
    |> List.choose (function
      | ForeignKey fk -> Some fk.refTable
      | _ -> None))
  |> Set.ofList

let private topologicalOrder
  (tableOrder: string list)
  (tableByName: Map<string, CreateTable>)
  (tableNames: Set<string>)
  : string list =
  let rankByName =
    tableOrder |> List.mapi (fun index name -> name, index) |> Map.ofList

  let rank name =
    rankByName.TryFind name |> Option.defaultValue Int32.MaxValue

  let mutable pending = tableNames
  let mutable doneTables = Set.empty<string>
  let ordered = ResizeArray<string>()

  while not pending.IsEmpty do
    let ready =
      pending
      |> Set.toList
      |> List.filter (fun tableName ->
        let dependencies =
          tableDependencies tableByName tableName |> Set.intersect tableNames

        Set.isSubset dependencies doneTables)
      |> List.sortBy rank

    match ready with
    | next :: _ ->
      ordered.Add next
      doneTables <- doneTables.Add next
      pending <- pending.Remove next
    | [] ->
      let fallback = pending |> Set.toList |> List.sortBy rank |> List.head
      ordered.Add fallback
      doneTables <- doneTables.Add fallback
      pending <- pending.Remove fallback

  ordered |> Seq.toList

let private extractSeedInserts
  (types: Type list)
  (boundValues: (string * obj) list)
  (schema: SqlFile)
  : Result<InsertInto list, string> =
  result {
    let tableRecordTypes =
      types |> List.filter isRecordType |> List.filter (isViewType >> not)

    let tableByType = Dictionary<Type, CreateTable>()

    for tableType in tableRecordTypes do
      let tableName = toSnakeCase tableType.Name

      match schema.tables |> List.tryFind (fun table -> table.name = tableName) with
      | Some table -> tableByType[tableType] <- table
      | None -> ()

    let seedObjects = toSeedRecordObjects tableByType boundValues

    let! seedRows =
      seedObjects
      |> foldResults
        (fun rows seedObject ->
          result {
            let recordType = seedObject.GetType()
            let! tableName, rowValues = createSeedRow tableByType recordType seedObject
            return rows @ [ tableName, rowValues ]
          })
        []

    let groupedRows =
      seedRows
      |> List.groupBy fst
      |> List.map (fun (tableName, rows) ->
        let values = rows |> List.map snd
        tableName, values)
      |> Map.ofList

    let tableOrder = seedRows |> List.map fst |> List.distinct

    let tableByName =
      schema.tables |> List.map (fun table -> table.name, table) |> Map.ofList

    let orderedTableNames =
      groupedRows.Keys |> Set.ofSeq |> topologicalOrder tableOrder tableByName

    let inserts =
      orderedTableNames
      |> List.map (fun tableName ->
        let table = tableByName[tableName]

        { table = tableName
          columns = table.columns |> List.map _.name
          values = groupedRows[tableName] })

    return inserts
  }

let internal buildSchemaFromScript (scriptPath: string) : Result<SqlFile, string> =
  result {
    let! reflectedTypes, boundValues = evaluateScript scriptPath
    let! schema = buildSchemaFromTypes reflectedTypes
    let! inserts = extractSeedInserts reflectedTypes boundValues schema

    return { schema with inserts = inserts }
  }
