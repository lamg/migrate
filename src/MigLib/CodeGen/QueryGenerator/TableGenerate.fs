module internal Mig.CodeGen.QueryGeneratorTableGenerate

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorTableCrud
open Mig.CodeGen.QueryGeneratorTableQueryExtensions
open Mig.CodeGen.QueryGeneratorCommon

let generateTableCode (table: CreateTable) : Result<string, string> =
  let typeName = capitalizeName table.name
  let upsertValidationResult = validateUpsertAnnotation table

  let queryByValidationResults =
    table.queryByAnnotations |> List.map (validateQueryByAnnotation table)

  let queryLikeValidationResults =
    table.queryLikeAnnotations |> List.map (validateQueryLikeAnnotation table)

  let queryByOrCreateValidationResults =
    table.queryByOrCreateAnnotations
    |> List.map (validateQueryByOrCreateAnnotation table)

  let firstError =
    ([ upsertValidationResult ]
     @ queryByValidationResults
     @ queryLikeValidationResults
     @ queryByOrCreateValidationResults)
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
    let getOneMethod = generateGetOne table
    let updateMethod = generateUpdate table
    let deleteMethod = generateDelete table

    let deleteAllMethod =
      if table.deleteAllAnnotations.IsEmpty then
        None
      else
        generateDeleteAll table

    let queryByMethods = table.queryByAnnotations |> List.map (generateQueryBy table)

    let queryLikeMethods =
      table.queryLikeAnnotations |> List.map (generateQueryLike table)

    let queryByOrCreateMethods =
      table.queryByOrCreateAnnotations |> List.map (generateQueryByOrCreate table)

    let allMethods =
      [ Some insertMethod
        insertOrIgnoreMethod
        upsertMethod
        getMethod
        Some getAllMethod
        Some getOneMethod
        updateMethod
        deleteMethod
        deleteAllMethod ]
      @ (queryByMethods |> List.map Some)
      @ (queryLikeMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id

    Ok(generateAugmentationCode typeName allMethods)
