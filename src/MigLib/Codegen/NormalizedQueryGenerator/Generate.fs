module internal MigLib.Codegen.NormalizedQueryGeneratorGenerate

open MigLib.Schema.Types
open MigLib.Codegen
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.NormalizedSchema
open MigLib.Codegen.NormalizedQueryGeneratorCommon
open MigLib.Codegen.NormalizedQueryGeneratorInsertSelect
open MigLib.Codegen.NormalizedQueryGeneratorUpdateDelete
open MigLib.Codegen.NormalizedQueryGeneratorQueryExtensions

let generateNormalizedTableCode (normalized: NormalizedTable) : Result<string, string> =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let queryByValidationResults =
    normalized.baseTable.queryByAnnotations
    |> List.map (validateNormalizedQueryByAnnotation normalized)

  let selectOneByValidationResults =
    normalized.baseTable.selectOneByAnnotations
    |> List.map (validateNormalizedSelectOneByAnnotation normalized)

  let queryLikeValidationResults =
    normalized.baseTable.queryLikeAnnotations
    |> List.map (validateNormalizedQueryLikeAnnotation normalized)

  let queryWhereValidationResults =
    normalized.baseTable.queryWhereAnnotations
    |> List.map (validateNormalizedQueryWhereAnnotation normalized)

  let queryByOrCreateValidationResults =
    normalized.baseTable.queryByOrCreateAnnotations
    |> List.map (validateNormalizedQueryByOrCreateAnnotation normalized)

  let deleteWhereValidationResults =
    normalized.baseTable.deleteWhereAnnotations
    |> List.map (validateNormalizedDeleteWhereAnnotation normalized)

  let firstError =
    queryByValidationResults
    @ selectOneByValidationResults
    @ queryLikeValidationResults
    @ queryWhereValidationResults
    @ queryByOrCreateValidationResults
    @ deleteWhereValidationResults
    |> List.tryFind (function
      | Error _ -> true
      | _ -> false)

  match firstError with
  | Some(Error msg) -> Error msg
  | _ ->
    let insertMethod = generateInsert normalized

    let insertOrIgnoreMethod =
      if normalized.baseTable.insertOrIgnoreAnnotations.IsEmpty then
        None
      else
        Some(generateInsertOrIgnore normalized)

    let getAllMethod = generateGetAll normalized
    let getByIdMethod = generateGetById normalized
    let getOneMethod = generateGetOne normalized
    let updateMethod = generateUpdate normalized
    let deleteMethod = generateDelete normalized

    let upsertMethod =
      if normalized.baseTable.upsertAnnotations.IsEmpty then
        None
      else
        generateUpsert normalized

    let deleteAllMethod =
      if normalized.baseTable.deleteAllAnnotations.IsEmpty then
        None
      else
        Some(generateDeleteAll normalized)

    let deleteWhereMethods =
      normalized.baseTable.deleteWhereAnnotations
      |> List.map (generateDeleteWhere normalized)

    let queryByMethods =
      normalized.baseTable.queryByAnnotations
      |> List.map (generateNormalizedQueryBy normalized)

    let selectOneByMethods =
      normalized.baseTable.selectOneByAnnotations
      |> List.map (generateNormalizedSelectOneBy normalized)

    let queryLikeMethods =
      normalized.baseTable.queryLikeAnnotations
      |> List.map (generateNormalizedQueryLike normalized)

    let queryWhereMethods =
      normalized.baseTable.queryWhereAnnotations
      |> List.map (generateNormalizedQueryWhere normalized)

    let queryByOrCreateMethods =
      normalized.baseTable.queryByOrCreateAnnotations
      |> List.map (generateNormalizedQueryByOrCreate normalized)

    let allMethods =
      [ Some insertMethod
        insertOrIgnoreMethod
        Some getAllMethod
        getByIdMethod
        Some getOneMethod
        updateMethod
        upsertMethod
        deleteMethod
        deleteAllMethod ]
      @ (deleteWhereMethods |> List.map Some)
      @ (queryByMethods |> List.map Some)
      @ (selectOneByMethods |> List.map Some)
      @ (queryLikeMethods |> List.map Some)
      @ (queryWhereMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id

    Ok(generateAugmentationCode typeName allMethods)
