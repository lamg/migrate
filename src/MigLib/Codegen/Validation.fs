module internal MigLib.Codegen.Validation

let validateAnnotationColumns
  (annotationName: string)
  (objectKind: string)
  (objectName: string)
  (availableColumns: string list)
  (referencedColumns: string list)
  : Result<unit, string> =
  let availableColumnSet =
    availableColumns
    |> List.map (fun column -> column.ToLowerInvariant())
    |> Set.ofList

  referencedColumns
  |> List.tryFind (fun column -> not (availableColumnSet.Contains(column.ToLowerInvariant())))
  |> function
    | Some invalidColumn ->
      let availableColumnText = availableColumns |> String.concat ", "

      Error
        $"{annotationName} annotation references non-existent column '{invalidColumn}' in {objectKind} '{objectName}'. Available columns: {availableColumnText}"
    | None -> Ok()
