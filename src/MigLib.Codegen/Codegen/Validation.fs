namespace MigLib.Codegen

module internal Validation =
  open System
  open MigLib.Codegen.Naming
  open MigLib.Codegen.Types

  let private columnExists (rel: RelationInfo) (column: string) =
    rel.columns
    |> List.exists (fun c -> c.name.Equals(column, StringComparison.OrdinalIgnoreCase))

  let private resolveColumnName (rel: RelationInfo) (column: string) =
    rel.columns
    |> List.tryFind (fun c -> c.name.Equals(column, StringComparison.OrdinalIgnoreCase))
    |> Option.map _.name

  let private validateOp (rel: RelationInfo) (op: Op) : Result<unit, string> =
    let requireSinglePk () =
      match rel.PrimaryKeyColumns with
      | [ _ ] -> Ok()
      | [] -> Error $"relation '{rel.name}' has no primary key (required for this op)"
      | _ -> Error $"relation '{rel.name}' has a composite primary key; use select_by/delete_by instead of *_by_id"

    let requireColumns cols =
      let missing = cols |> List.filter (fun c -> not (columnExists rel c))

      if missing.IsEmpty then
        Ok()
      else
        let missingList = String.concat ", " missing
        Error $"unknown column(s) on '{rel.name}': {missingList}"

    match rel.kind, op with
    | RelationKind.View, op when op.IsWrite -> Error $"write op not allowed on view '{rel.name}': {op}"
    | _, Op.SelectById
    | _, Op.DeleteById -> requireSinglePk ()
    | _, Op.Upsert
    | _, Op.UpsertMany ->
      match rel.PrimaryKeyColumns with
      | [] -> Error $"upsert requires a primary key on '{rel.name}'"
      | _ -> Ok()
    | _, Op.SelectBy cols
    | _, Op.SelectOneBy cols
    | _, Op.SelectByOrInsert cols
    | _, Op.DeleteBy cols -> requireColumns cols
    | _, Op.SelectLike col -> requireColumns [ col ]
    | _, Op.SelectTop(col, limit)
    | _, Op.SelectBottom(col, limit) ->
      if limit <= 0 then
        Error $"select_top/select_bottom limit must be positive (got {limit}) on '{rel.name}'"
      else
        requireColumns [ col ]
    | _, Op.SelectRange orderBy ->
      if orderBy.IsEmpty then
        Error $"select_range requires at least one order column on '{rel.name}'"
      else
        requireColumns (orderBy |> List.map fst)
    | _ -> Ok()

  /// Ensure bulk ops also emit their single-row companions.
  let private expandOps (ops: Op list) : Op list =
    let has op = ops |> List.contains op
    let acc = ResizeArray(ops)

    if has Op.UpsertMany && not (has Op.Upsert) then
      acc.Add Op.Upsert

    if has Op.InsertMany && not (has Op.Insert) && not (has Op.InsertOrIgnore) then
      acc.Add Op.Insert

    acc |> List.ofSeq

  let private normalizeOpColumns (rel: RelationInfo) (op: Op) : Result<Op, string> =
    let mapCols cols =
      let results =
        cols
        |> List.map (fun c ->
          match resolveColumnName rel c with
          | Some name -> Ok name
          | None -> Error c)

      let errors =
        results
        |> List.choose (function
          | Error e -> Some e
          | Ok _ -> None)

      if not errors.IsEmpty then
        let errorList = String.concat ", " errors
        Error $"unknown column(s) on '{rel.name}': {errorList}"
      else
        Ok(
          results
          |> List.choose (function
            | Ok n -> Some n
            | Error _ -> None)
        )

    match op with
    | Op.SelectBy cols -> mapCols cols |> Result.map Op.SelectBy
    | Op.SelectOneBy cols -> mapCols cols |> Result.map Op.SelectOneBy
    | Op.SelectByOrInsert cols -> mapCols cols |> Result.map Op.SelectByOrInsert
    | Op.DeleteBy cols -> mapCols cols |> Result.map Op.DeleteBy
    | Op.SelectLike col ->
      match resolveColumnName rel col with
      | Some name -> Ok(Op.SelectLike name)
      | None -> Error $"unknown column on '{rel.name}': {col}"
    | Op.SelectTop(col, limit) ->
      match resolveColumnName rel col with
      | Some name -> Ok(Op.SelectTop(name, limit))
      | None -> Error $"unknown column on '{rel.name}': {col}"
    | Op.SelectBottom(col, limit) ->
      match resolveColumnName rel col with
      | Some name -> Ok(Op.SelectBottom(name, limit))
      | None -> Error $"unknown column on '{rel.name}': {col}"
    | Op.SelectRange orderBy ->
      let results =
        orderBy
        |> List.map (fun (col, dir) ->
          match resolveColumnName rel col with
          | Some name -> Ok(name, dir)
          | None -> Error col)

      let errors =
        results
        |> List.choose (function
          | Error e -> Some e
          | Ok _ -> None)

      if not errors.IsEmpty then
        let errorList = String.concat ", " errors
        Error $"unknown column(s) on '{rel.name}': {errorList}"
      else
        Ok(
          Op.SelectRange(
            results
            |> List.choose (function
              | Ok o -> Some o
              | Error _ -> None)
          )
        )
    | other -> Ok other

  let private processAnnotation
    (ann: RelationAnnotation)
    (rel: RelationInfo)
    (errors: ResizeArray<string>)
    (results: ResizeArray<AnnotatedRelation>)
    (usedFsNames: System.Collections.Generic.HashSet<string>)
    =
    if ann.ops.IsEmpty then
      ()
    else
      let opResults =
        ann.ops
        |> List.map (fun op ->
          match validateOp rel op with
          | Error e -> Error e
          | Ok() -> normalizeOpColumns rel op)

      let opErrors =
        opResults
        |> List.choose (function
          | Error e -> Some e
          | Ok _ -> None)

      if not opErrors.IsEmpty then
        for e in opErrors do
          errors.Add $"{ann.sourceFile}:{ann.sourceLine}: {e}"
      else
        let ops =
          opResults
          |> List.choose (function
            | Ok o -> Some o
            | Error _ -> None)
          |> expandOps

        let overrideMap =
          System.Collections.Generic.Dictionary<string, ColumnOverrideKind>(StringComparer.OrdinalIgnoreCase)

        let mutable overrideOk = true

        for ov in ann.overrides do
          match resolveColumnName rel ov.column with
          | None ->
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: override column '{ov.column}' not found on '{rel.name}'"
            overrideOk <- false
          | Some realName ->
            if overrideMap.ContainsKey realName then
              errors.Add $"{ann.sourceFile}:{ann.sourceLine}: duplicate override for column '{realName}'"
              overrideOk <- false
            else
              overrideMap[realName] <- ov.kind

        if overrideOk then
          let fsName =
            match ann.fsNameOverride with
            | Some name -> sanitizeFsIdent name
            | None -> sanitizeFsIdent (toPascalCase rel.name)

          if not (usedFsNames.Add fsName) then
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: duplicate F# relation name '{fsName}'"
          else
            let overrides = overrideMap |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

            results.Add
              { sqlName = rel.name
                fsName = fsName
                kind = rel.kind
                columns = rel.columns
                ops = ops
                overrides = overrides }

  let merge
    (schema: Map<string, RelationInfo>)
    (annotations: RelationAnnotation list)
    : Result<AnnotatedRelation list, string> =
    let errors = ResizeArray<string>()
    let results = ResizeArray<AnnotatedRelation>()
    let usedFsNames = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

    for ann in annotations do
      match ann.sqlName with
      | None -> errors.Add $"{ann.sourceFile}:{ann.sourceLine}: annotation missing SQL relation name"
      | Some sqlName ->
        match schema.TryFind sqlName with
        | Some rel -> processAnnotation ann rel errors results usedFsNames
        | None ->
          let matchKey =
            schema.Keys
            |> Seq.tryFind (fun k -> k.Equals(sqlName, StringComparison.OrdinalIgnoreCase))

          match matchKey with
          | None ->
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: relation '{sqlName}' not found in schema after migrations"
          | Some key -> processAnnotation ann schema[key] errors results usedFsNames

    if errors.Count > 0 then
      Error(String.concat Environment.NewLine (errors |> Seq.toList))
    else
      Ok(results |> List.ofSeq)
