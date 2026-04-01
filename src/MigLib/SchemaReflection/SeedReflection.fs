namespace Mig

open System
open System.Collections.Generic
open System.Globalization
open System.Reflection
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types
open MigLib.Util

open SchemaReflectionNaming
open SchemaReflectionAttributes
open SchemaReflectionAssembly

module internal SchemaReflectionSeed =
  let isTypeUnderModuleName (moduleName: string) (candidate: Type) =
    let fullName = candidate.FullName

    not (String.IsNullOrWhiteSpace fullName)
    && (fullName.StartsWith(moduleName + "+", StringComparison.Ordinal)
        || fullName.Contains("." + moduleName + "+", StringComparison.Ordinal))

  let tryFindModuleType (assembly: Assembly) (moduleName: string) =
    assembly.GetTypes()
    |> Array.tryFind (fun candidate ->
      let fullName = candidate.FullName

      not (String.IsNullOrWhiteSpace fullName)
      && (String.Equals(fullName, moduleName, StringComparison.Ordinal)
          || fullName.EndsWith($".{moduleName}", StringComparison.Ordinal)))

  let getStaticModuleSeedValues (moduleType: Type) (recordTypes: Type list) =
    let flags =
      BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

    let recordTypeSet = HashSet<Type>(recordTypes)

    let properties =
      moduleType.GetProperties flags
      |> Array.filter (fun propertyInfo ->
        propertyInfo.GetIndexParameters().Length = 0
        && recordTypeSet.Contains propertyInfo.PropertyType)
      |> Array.map (fun propertyInfo -> propertyInfo.Name, propertyInfo.PropertyType, propertyInfo.GetValue null)

    let propertyNames =
      properties |> Array.map (fun (name, _, _) -> name) |> Set.ofArray

    let fields =
      moduleType.GetFields flags
      |> Array.filter (fun fieldInfo ->
        not fieldInfo.IsSpecialName
        && not (propertyNames.Contains fieldInfo.Name)
        && recordTypeSet.Contains fieldInfo.FieldType)
      |> Array.map (fun fieldInfo -> fieldInfo.Name, fieldInfo.FieldType, fieldInfo.GetValue null)

    Array.append properties fields
    |> Array.sortBy (fun (name, _, _) -> name)
    |> Array.toList

  let rec toSeedExpr (recordTypes: Type list) (fieldType: Type) (value: obj) : Result<Expr, string> =
    if isNull value then
      Ok(Value "NULL")
    else
      match mapSupportedScalarType fieldType with
      | Some(SqlInteger, _) ->
        let int64Value = unbox<int64> value

        if int64Value >= int64 Int32.MinValue && int64Value <= int64 Int32.MaxValue then
          Ok(Integer(int int64Value))
        else
          Ok(Value(int64Value.ToString(CultureInfo.InvariantCulture)))
      | Some(SqlText, Some _) ->
        let unionCase, unionFields = FSharpValue.GetUnionFields(value, fieldType, true)

        if unionFields.Length = 0 then
          Ok(String unionCase.Name)
        else
          Error $"Seed value for type '{fieldType.Name}' must be an enum-like union with no payload fields."
      | Some(SqlText, None) -> Ok(String(unbox<string> value))
      | Some(SqlReal, _) -> Ok(Real(unbox<float> value))
      | Some(SqlTimestamp, _) -> Ok(String(string value))
      | Some(SqlString, _) -> Ok(String(string value))
      | Some(SqlFlexible, _) -> Ok(Value(string value))
      | None when recordTypes |> List.contains fieldType ->
        result {
          let! primaryKey =
            readPrimaryKeyInfo fieldType
            |> Result.bind (function
              | Some value -> Ok value
              | None -> Error $"Seed value for nested record type '{fieldType.Name}' requires a primary key.")

          let primaryKeyField =
            FSharpType.GetRecordFields(fieldType, true)
            |> Array.tryFind (fun field ->
              String.Equals(toSnakeCase field.Name, primaryKey.columnName, StringComparison.Ordinal))

          match primaryKeyField with
          | None ->
            return!
              Error
                $"Seed value for nested record type '{fieldType.Name}' could not resolve primary key field '{primaryKey.columnName}'."
          | Some field ->
            let primaryKeyValue = field.GetValue value
            return! toSeedExpr recordTypes field.PropertyType primaryKeyValue
        }
      | None -> Error $"Seed value field type '{fieldType.Name}' is not supported."

  let toSeedColumnValue
    (recordTypes: Type list)
    (field: PropertyInfo)
    (recordValue: obj)
    : Result<string * Expr, string> =
    result {
      let value = field.GetValue recordValue

      if recordTypes |> List.contains field.PropertyType then
        let! expr = toSeedExpr recordTypes field.PropertyType value
        return $"{toSnakeCase field.Name}_id", expr
      else
        let! expr = toSeedExpr recordTypes field.PropertyType value
        return toSnakeCase field.Name, expr
    }

  let buildSeedInsert (recordTypes: Type list) (recordType: Type) (recordValue: obj) : Result<InsertInto, string> =
    result {
      let! columnValues =
        FSharpType.GetRecordFields(recordType, true)
        |> Array.toList
        |> foldResults
          (fun values field ->
            result {
              let! columnValue = toSeedColumnValue recordTypes field recordValue
              return values @ [ columnValue ]
            })
          []

      return
        { table = toSnakeCase recordType.Name
          columns = columnValues |> List.map fst
          values = [ columnValues |> List.map snd ] }
    }

  let mergeSeedInserts (inserts: InsertInto list) =
    inserts
    |> List.groupBy (fun insert -> insert.table, insert.columns)
    |> List.map (fun ((table, columns), (group: InsertInto list)) ->
      { table = table
        columns = columns
        values = group |> List.collect (fun insert -> insert.values) })

  let readSeedInsertsFromModule
    (assembly: Assembly)
    (moduleName: string)
    (recordTypes: Type list)
    : Result<InsertInto list, string> =
    match tryFindModuleType assembly moduleName with
    | None -> Ok []
    | Some moduleType ->
      getStaticModuleSeedValues moduleType recordTypes
      |> foldResults
        (fun inserts (_, recordType, recordValue) ->
          result {
            let! insert = buildSeedInsert recordTypes recordType recordValue
            return inserts @ [ insert ]
          })
        []
      |> Result.map mergeSeedInserts

  let buildSchemaFromAssemblyModule (assembly: Assembly) (moduleName: string) : Result<SqlFile, string> =
    if String.IsNullOrWhiteSpace moduleName then
      Error "Schema module name cannot be empty."
    else
      let types =
        assembly.GetTypes()
        |> Array.filter (fun t -> t.Assembly = assembly)
        |> Array.filter (fun t -> isTypeUnderModuleName moduleName t)
        |> Array.filter (fun t -> isRecordType t || isUnionType t)
        |> Array.toList

      if types.IsEmpty then
        Error $"No record or union schema types were found under compiled module '{moduleName}'."
      else
        result {
          let recordTypes = types |> List.filter isRecordType
          let! schema = buildSchemaFromTypes types
          let! inserts = readSeedInsertsFromModule assembly moduleName recordTypes

          return
            { schema with
                inserts = schema.inserts @ inserts }
        }
