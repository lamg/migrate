module internal MigLib.Codegen.NormalizedTypeGenerator

open MigLib.Schema.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open MigLib.Codegen
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.NormalizedSchema

let private getInsertColumns (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    not (
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)
    ))

let private aspectToPascalCase (aspect: string) : string = TypeGenerator.toPascalCase aspect

let private getBaseCaseColumns (baseTable: CreateTable) (includeAutoIncrementPk: bool) : ColumnDef list =
  if includeAutoIncrementPk then
    baseTable.columns
  else
    getInsertColumns baseTable

let private getExtensionCaseColumns
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (includeAutoIncrementPk: bool)
  : ColumnDef list =
  let baseColumns = getBaseCaseColumns baseTable includeAutoIncrementPk
  let extensionFkColumns = extension.fkColumns |> Set.ofList

  let extensionColumns =
    extension.table.columns
    |> List.filter (fun col -> not (extensionFkColumns.Contains col.name))

  baseColumns @ extensionColumns

let generateNewType (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"

  let generateCaseWidget caseName (columns: ColumnDef list) =
    let fields =
      columns
      |> List.map (fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name
        let fsharpType = TypeGenerator.mapColumnType col
        Ast.Field(fieldName, fsharpType))

    UnionCase(caseName, fields)

  Oak() {
    AnonymousModule() {
      (Union(newTypeName) {
        let baseColumns = getBaseCaseColumns normalized.baseTable false
        generateCaseWidget "Base" baseColumns

        for ext in normalized.extensions do
          let caseName = $"With{aspectToPascalCase ext.aspectName}"
          let columns = getExtensionCaseColumns normalized.baseTable ext false
          generateCaseWidget caseName columns
      })
        .attribute (Attribute("RequireQualifiedAccess"))
    }
  }
  |> Gen.mkOak
  |> Gen.run

let generateQueryType (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let generateCaseWidget caseName (columns: ColumnDef list) =
    let fields =
      columns
      |> List.map (fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name
        let fsharpType = TypeGenerator.mapColumnType col
        Ast.Field(fieldName, fsharpType))

    UnionCase(caseName, fields)

  Oak() {
    AnonymousModule() {
      (Union(typeName) {
        let baseColumns = getBaseCaseColumns normalized.baseTable true
        generateCaseWidget "Base" baseColumns

        for ext in normalized.extensions do
          let caseName = $"With{aspectToPascalCase ext.aspectName}"
          let columns = getExtensionCaseColumns normalized.baseTable ext true
          generateCaseWidget caseName columns
      })
        .attribute (Attribute("RequireQualifiedAccess"))
    }
  }
  |> Gen.mkOak
  |> Gen.run

type private FieldInfo =
  { Name: string
    FSharpType: string
    InAllCases: bool }

let private collectFields (normalized: NormalizedTable) (includeAutoIncrementPk: bool) : FieldInfo list =
  let baseCaseColumns = getBaseCaseColumns normalized.baseTable includeAutoIncrementPk

  let extensionCasesColumns =
    normalized.extensions
    |> List.map (fun ext -> getExtensionCaseColumns normalized.baseTable ext includeAutoIncrementPk)

  let allCases = baseCaseColumns :: extensionCasesColumns
  let totalCases = allCases.Length

  let fieldMap =
    allCases
    |> List.collect (fun columns ->
      columns
      |> List.map (fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name
        let fsharpType = TypeGenerator.mapColumnType col
        fieldName, fsharpType))
    |> List.groupBy fst
    |> List.map (fun (fieldName, occurrences) ->
      let fsharpType = occurrences |> List.head |> snd
      let count = occurrences.Length

      fieldName,
      { Name = fieldName
        FSharpType = fsharpType
        InAllCases = count = totalCases })
    |> Map.ofList

  fieldMap |> Map.toList |> List.map snd

let private generateProperty (typeName: string) (field: FieldInfo) (normalized: NormalizedTable) =
  let returnType =
    if field.InAllCases then
      field.FSharpType
    else
      $"{field.FSharpType} option"

  let createMatchClause caseName (columns: ColumnDef list) =
    let hasField =
      columns
      |> List.exists (fun col -> TypeGenerator.toPascalCase col.name = field.Name)

    let pattern = LongIdentPat($"{typeName}.{caseName}", NamedPat("data"))

    if hasField then
      if field.InAllCases then
        MatchClauseExpr(pattern, ConstantExpr($"data.{field.Name}"))
      else
        MatchClauseExpr(pattern, AppExpr("Some", [ $"data.{field.Name}" ]))
    else
      MatchClauseExpr(LongIdentPat($"{typeName}.{caseName}", "_"), ConstantExpr("None"))

  let baseColumns = getBaseCaseColumns normalized.baseTable true
  let baseClause = createMatchClause "Base" baseColumns

  let extensionClauses =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{aspectToPascalCase ext.aspectName}"
      let columns = getExtensionCaseColumns normalized.baseTable ext true
      createMatchClause caseName columns)

  Member($"this.{field.Name}", MatchExpr("this", baseClause :: extensionClauses), returnType)

let private generateProperties (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let fields = collectFields normalized true

  if fields.IsEmpty then
    ""
  else
    fields
    |> List.map (fun field -> generateProperty typeName field normalized)
    |> generateAugmentationCode typeName
    |> fun code -> $"\n{code}"

let generateTypes (normalized: NormalizedTable) : string =
  let newType = generateNewType normalized
  let queryType = generateQueryType normalized
  let properties = generateProperties normalized

  let combined =
    if properties = "" then
      $"{newType}\n\n{queryType}"
    else
      $"{newType}\n\n{queryType}\n{properties}"

  FabulousAstHelpers.formatCode combined
