module internal MigLib.Codegen.QueryGeneratorViewGenerate

open MigLib.Schema.Types
open MigLib.Codegen.QueryGeneratorViewQueries
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.QueryGeneratorCommon

let generateViewCode (view: CreateView) (columns: ViewColumn list) : Result<string, string> =
  let typeName = capitalizeName view.name

  match
    view.queryByOrCreateAnnotations,
    view.insertOrIgnoreAnnotations,
    view.deleteWhereAnnotations,
    view.deleteAllAnnotations,
    view.upsertAnnotations
  with
  | [], [], [], [], [] ->
    let queryByValidationResults =
      view.queryByAnnotations
      |> List.map (validateViewQueryByAnnotation view.name columns)

    let selectOneByValidationResults =
      view.selectOneByAnnotations
      |> List.map (validateViewSelectOneByAnnotation view.name columns)

    let queryLikeValidationResults =
      view.queryLikeAnnotations
      |> List.map (validateViewQueryLikeAnnotation view.name columns)

    let queryWhereValidationResults =
      view.queryWhereAnnotations
      |> List.map (validateViewQueryWhereAnnotation view.name columns)

    let validationResults =
      queryByValidationResults
      @ selectOneByValidationResults
      @ queryLikeValidationResults
      @ queryWhereValidationResults

    let firstError =
      validationResults
      |> List.tryFind (function
        | Error _ -> true
        | _ -> false)

    match firstError with
    | Some(Error msg) -> Error msg
    | _ ->
      let getAllMethod = generateViewGetAll view.name columns

      let getOneMethod =
        if view.selectOneAnnotations.IsEmpty then
          []
        else
          [ generateViewGetOne view.name columns ]

      let queryByMethods =
        view.queryByAnnotations |> List.map (generateViewQueryBy view.name columns)

      let selectOneByMethods =
        view.selectOneByAnnotations
        |> List.map (generateViewSelectOneBy view.name columns)

      let queryLikeMethods =
        view.queryLikeAnnotations |> List.map (generateViewQueryLike view.name columns)

      let queryWhereMethods =
        view.queryWhereAnnotations
        |> List.map (generateViewQueryWhere view.name columns)

      let allMethods =
        getAllMethod :: getOneMethod
        @ queryByMethods
        @ selectOneByMethods
        @ queryLikeMethods
        @ queryWhereMethods

      Ok(generateAugmentationCode typeName allMethods)
  | _ :: _, _, _, _, _ ->
    Error
      $"QueryByOrCreate annotation is not supported on views (view '{view.name}' is read-only). Use QueryBy instead."
  | [], _ :: _, _, _, _ ->
    Error $"InsertOrIgnore annotation is not supported on views (view '{view.name}' is read-only)."
  | [], [], _ :: _, _, _ -> Error $"DeleteWhere annotation is not supported on views (view '{view.name}' is read-only)."
  | [], [], [], _ :: _, _ -> Error $"DeleteAll annotation is not supported on views (view '{view.name}' is read-only)."
  | [], [], [], [], _ :: _ -> Error $"Upsert annotation is not supported on views (view '{view.name}' is read-only)."
