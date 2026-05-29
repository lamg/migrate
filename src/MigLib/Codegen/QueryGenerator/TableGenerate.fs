module internal MigLib.Codegen.QueryGeneratorTableGenerate

open MigLib.Schema.Types
open MigLib.Codegen.QueryGeneratorTableCrud
open MigLib.Codegen.QueryGeneratorTableQueryExtensions
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.QueryGeneratorCommon

let generateTableCode (table: CreateTable) : Result<string, string> =
  let typeName = capitalizeName table.name
  let upsertValidationResult = validateUpsertAnnotation table

  let queryByValidationResults =
    table.queryByAnnotations |> List.map (validateQueryByAnnotation table)

  let queryLikeValidationResults =
    table.queryLikeAnnotations |> List.map (validateQueryLikeAnnotation table)

  let queryWhereValidationResults =
    table.queryWhereAnnotations |> List.map (validateQueryWhereAnnotation table)

  let queryByOrCreateValidationResults =
    table.queryByOrCreateAnnotations
    |> List.map (validateQueryByOrCreateAnnotation table)

  let deleteWhereValidationResults =
    table.deleteWhereAnnotations |> List.map (validateDeleteWhereAnnotation table)

  let firstError =
    ([ upsertValidationResult ]
     @ queryByValidationResults
     @ queryLikeValidationResults
     @ queryWhereValidationResults
     @ queryByOrCreateValidationResults
     @ deleteWhereValidationResults)
    |> List.tryFind (function
      | Error _ -> true
      | _ -> false)

  match firstError with
  | Some(Error msg) -> Error msg
  | _ ->
    let insertMethod = generateInsert table

    let insertOrIgnoreMethod =
      if table.insertOrIgnoreAnnotations.IsEmpty then
        None
      else
        Some(generateInsertOrIgnore table)

    let upsertMethod =
      if table.upsertAnnotations.IsEmpty then
        None
      else
        generateUpsert table

    let getMethod = generateGet table
    let getAllMethod = generateGetAll table

    let getOneMethod =
      if table.selectOneAnnotations.IsEmpty then
        None
      else
        Some(generateGetOne table)

    let updateMethod = generateUpdate table
    let deleteMethod = generateDelete table

    let deleteAllMethod =
      if table.deleteAllAnnotations.IsEmpty then
        None
      else
        generateDeleteAll table

    let deleteWhereMethods =
      table.deleteWhereAnnotations |> List.map (generateDeleteWhere table)

    let queryByMethods = table.queryByAnnotations |> List.map (generateQueryBy table)

    let queryLikeMethods =
      table.queryLikeAnnotations |> List.map (generateQueryLike table)

    let queryWhereMethods =
      table.queryWhereAnnotations |> List.map (generateQueryWhere table)

    let queryByOrCreateMethods =
      table.queryByOrCreateAnnotations |> List.map (generateQueryByOrCreate table)

    let allMethods =
      [
        Some insertMethod
        insertOrIgnoreMethod
        upsertMethod
        getMethod
        Some getAllMethod
        getOneMethod
        updateMethod
        deleteMethod
        deleteAllMethod
      ]
      @ (deleteWhereMethods |> List.map Some)
      @ (queryByMethods |> List.map Some)
      @ (queryLikeMethods |> List.map Some)
      @ (queryWhereMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id

    Ok(generateAugmentationCode typeName allMethods)
