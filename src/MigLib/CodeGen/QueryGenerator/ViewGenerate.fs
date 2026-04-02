module internal Mig.CodeGen.QueryGeneratorViewGenerate

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.ViewIntrospection
open Mig.CodeGen.QueryGeneratorCommon
open Mig.CodeGen.QueryGeneratorViewQueries

let generateViewCode (view: CreateView) (columns: ViewColumn list) : Result<string, string> =
  let typeName = capitalizeName view.name

  match view.queryByOrCreateAnnotations, view.insertOrIgnoreAnnotations, view.upsertAnnotations with
  | [], [], [] ->
    let queryByValidationResults =
      view.queryByAnnotations
      |> List.map (validateViewQueryByAnnotation view.name columns)

    let queryLikeValidationResults =
      view.queryLikeAnnotations
      |> List.map (validateViewQueryLikeAnnotation view.name columns)

    let validationResults = queryByValidationResults @ queryLikeValidationResults

    let firstError =
      validationResults
      |> List.tryFind (function
        | Error _ -> true
        | _ -> false)

    match firstError with
    | Some(Error msg) -> Error msg
    | _ ->
      let getAllMethod = generateViewGetAll view.name columns
      let getOneMethod = generateViewGetOne view.name columns

      let queryByMethods =
        view.queryByAnnotations |> List.map (generateViewQueryBy view.name columns)

      let queryLikeMethods =
        view.queryLikeAnnotations |> List.map (generateViewQueryLike view.name columns)

      let allMethods =
        [ getAllMethod; getOneMethod ] @ queryByMethods @ queryLikeMethods

      Ok(generateAugmentationCode typeName allMethods)
  | _ :: _, _, _ ->
    Error
      $"QueryByOrCreate annotation is not supported on views (view '{view.name}' is read-only). Use QueryBy instead."
  | [], _ :: _, _ -> Error $"InsertOrIgnore annotation is not supported on views (view '{view.name}' is read-only)."
  | [], [], _ :: _ -> Error $"Upsert annotation is not supported on views (view '{view.name}' is read-only)."
