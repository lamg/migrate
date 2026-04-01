namespace Mig

open System
open System.Collections.Generic
open System.Reflection
open DeclarativeMigrations.Types
open MigLib.Db
open MigLib.Util

open SchemaReflectionNaming
open SchemaReflectionAttributes
open SchemaReflectionTable
open SchemaReflectionView
open SchemaReflectionUnionExtensions

module internal SchemaReflectionAssembly =
  let ensureUniqueTableNames (tables: CreateTable list) : Result<unit, string> =
    let duplicates =
      tables
      |> List.groupBy _.name
      |> List.filter (fun (_, grouped) -> grouped.Length > 1)

    if duplicates.IsEmpty then
      Ok()
    else
      let names = duplicates |> List.map fst |> String.concat ", "
      Error $"Schema produced duplicate table names: {names}"

  let buildSchemaFromTypes (types: Type list) : Result<SqlFile, string> =
    if types.IsEmpty then
      Error "No types were provided for schema reflection"
    else
      result {
        let schemaTypes = HashSet<Type> types

        let tableRecordTypes =
          types
          |> List.filter isRecordType
          |> List.filter (fun t ->
            getTypeAttributes<ViewAttribute> t |> List.isEmpty
            && getTypeAttributes<ViewSqlAttribute> t |> List.isEmpty)

        let viewTypes =
          types
          |> List.filter isRecordType
          |> List.filter (fun t ->
            getTypeAttributes<ViewAttribute> t |> List.isEmpty |> not
            || getTypeAttributes<ViewSqlAttribute> t |> List.isEmpty |> not)

        let unionTypes = types |> List.filter isUnionType

        let! pkByType =
          tableRecordTypes
          |> foldResults
            (fun pairs recordType ->
              result {
                let! pkInfo = readPrimaryKeyInfo recordType

                match pkInfo with
                | Some pk -> return pairs @ [ recordType, pk ]
                | None -> return pairs
              })
            []
          |> Result.map (fun pairs ->
            let dictionary = Dictionary<Type, PrimaryKeyInfo>()

            for key, value in pairs do
              dictionary[key] <- value

            dictionary)

        let typeToTableName = Dictionary<Type, string>()

        for tableType in tableRecordTypes do
          typeToTableName[tableType] <- toSnakeCase tableType.Name

        let! tableResults =
          tableRecordTypes
          |> foldResults
            (fun results recordType ->
              result {
                let! table = buildTable schemaTypes pkByType recordType
                return results @ [ table ]
              })
            []

        let reflectedTables = tableResults |> List.map fst

        let reflectedIndexes =
          tableResults
          |> List.collect snd
          |> List.distinctBy (fun index -> index.name, index.table, index.columns)

        let! extensionTables =
          unionTypes
          |> foldResults
            (fun allTables unionType ->
              result {
                let! extensionSet = buildUnionExtensionTables schemaTypes pkByType unionType
                return allTables @ extensionSet
              })
            []

        let allTables = reflectedTables @ extensionTables

        do! ensureUniqueTableNames allTables

        let tablesByName =
          allTables |> List.map (fun table -> table.name, table) |> Map.ofList

        let! views =
          viewTypes
          |> foldResults
            (fun xs viewType ->
              result {
                let! view = buildView typeToTableName tablesByName viewType
                return xs @ [ view ]
              })
            []

        return
          { emptyFile with
              tables = allTables
              indexes = reflectedIndexes
              views = views }
      }

  let buildSchemaFromAssembly (assembly: Assembly) : Result<SqlFile, string> =
    let types =
      assembly.GetTypes()
      |> Array.filter (fun t -> t.Assembly = assembly)
      |> Array.filter (fun t -> isRecordType t || isUnionType t)
      |> Array.toList

    buildSchemaFromTypes types
