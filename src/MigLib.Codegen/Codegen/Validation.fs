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
    | RelationKind.Table, Op.DeleteMatching _ ->
      Error $"delete_matching is only allowed on views (got table '{rel.name}')"
    | RelationKind.View, Op.DeleteMatching(_, key) -> requireColumns [ key ]
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
    | _, Op.SelectRange orderBy
    | _, Op.FilterSearch orderBy ->
      if orderBy.IsEmpty then
        Error $"order list requires at least one column on '{rel.name}'"
      else
        requireColumns (orderBy |> List.map fst)
    | RelationKind.Table, Op.SelectWith _ -> Error $"select_with is only allowed on views (got table '{rel.name}')"
    | RelationKind.View, Op.SelectWith args ->
      if args.IsEmpty then
        Error $"select_with requires at least one argument on '{rel.name}'"
      else
        Ok()
    | _, Op.FilterCount -> Ok()
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
    | Op.SelectRange orderBy
    | Op.FilterSearch orderBy ->
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
        let orderByResolved =
          results
          |> List.choose (function
            | Ok o -> Some o
            | Error _ -> None)

        match op with
        | Op.FilterSearch _ -> Ok(Op.FilterSearch orderByResolved)
        | _ -> Ok(Op.SelectRange orderByResolved)
    | Op.DeleteMatching(table, key) ->
      match resolveColumnName rel key with
      | Some name -> Ok(Op.DeleteMatching(table, name))
      | None -> Error $"unknown column on '{rel.name}': {key}"
    | other -> Ok other

  let private findSchemaRelation (schema: Map<string, RelationInfo>) (name: string) =
    match schema.TryFind name with
    | Some r -> Some r
    | None ->
      schema.Keys
      |> Seq.tryFind (fun k -> k.Equals(name, StringComparison.OrdinalIgnoreCase))
      |> Option.map (fun k -> schema[k])

  /// Cross-relation checks for delete_matching (target table + key, no select_with).
  let private validateDeleteMatchingOps
    (schema: Map<string, RelationInfo>)
    (rel: RelationInfo)
    (ops: Op list)
    : Result<Op list, string list> =
    let hasSelectWith =
      ops
      |> List.exists (function
        | Op.SelectWith _ -> true
        | _ -> false)

    let hasDeleteMatching =
      ops
      |> List.exists (function
        | Op.DeleteMatching _ -> true
        | _ -> false)

    if hasSelectWith && hasDeleteMatching then
      Error [ $"delete_matching cannot be combined with select_with on '{rel.name}' (catalog view has no bind params)" ]
    else
      let results =
        ops
        |> List.map (fun op ->
          match op with
          | Op.DeleteMatching(targetTable, key) ->
            match findSchemaRelation schema targetTable with
            | None -> Error $"delete_matching target table '{targetTable}' not found (from view '{rel.name}')"
            | Some target when target.kind <> RelationKind.Table ->
              Error $"delete_matching target '{target.name}' must be a table (from view '{rel.name}')"
            | Some target ->
              match resolveColumnName target key with
              | None ->
                Error $"delete_matching key '{key}' not found on target table '{target.name}' (from view '{rel.name}')"
              | Some targetKey -> Ok(Op.DeleteMatching(target.name, targetKey))
          | other -> Ok other)

      let errs =
        results
        |> List.choose (function
          | Error e -> Some e
          | Ok _ -> None)

      if not errs.IsEmpty then
        Error errs
      else
        Ok(
          results
          |> List.choose (function
            | Ok o -> Some o
            | Error _ -> None)
        )

  let private resolveFilters (rel: RelationInfo) (filters: FilterDef list) : Result<FilterDef list, string list> =
    let errors = ResizeArray<string>()

    let seenNames =
      System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let resolved = ResizeArray<FilterDef>()

    for f in filters do
      if not (seenNames.Add f.name) then
        errors.Add $"duplicate filter name '{f.name}' on '{rel.name}'"
      else
        let colResults =
          f.columns
          |> List.map (fun c ->
            match resolveColumnName rel c with
            | Some name -> Ok name
            | None -> Error c)

        let missing =
          colResults
          |> List.choose (function
            | Error e -> Some e
            | Ok _ -> None)

        if not missing.IsEmpty then
          let missingList = String.concat ", " missing
          errors.Add $"filter '{f.name}' unknown column(s) on '{rel.name}': {missingList}"
        else
          let cols =
            colResults
            |> List.choose (function
              | Ok n -> Some n
              | Error _ -> None)

          match f.kind, cols with
          | FilterKind.EqAny, cs when cs.Length < 2 ->
            errors.Add $"filter '{f.name}' kind eq_any requires at least two columns on '{rel.name}'"
          | FilterKind.EqAny, _ ->
            resolved.Add
              { name = f.name
                kind = f.kind
                columns = cols }
          | _, [ _ ] ->
            resolved.Add
              { name = f.name
                kind = f.kind
                columns = cols }
          | _, _ -> errors.Add $"filter '{f.name}' requires exactly one column on '{rel.name}'"

    if errors.Count > 0 then
      Error(errors |> Seq.toList)
    else
      Ok(resolved |> List.ofSeq)

  let private hasFilterSearch (ops: Op list) =
    ops
    |> List.exists (function
      | Op.FilterSearch _ -> true
      | _ -> false)

  let private hasFilterCount (ops: Op list) = ops |> List.contains Op.FilterCount

  let private processAnnotation
    (schema: Map<string, RelationInfo>)
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
        let expandedOps =
          opResults
          |> List.choose (function
            | Ok o -> Some o
            | Error _ -> None)
          |> expandOps

        match validateDeleteMatchingOps schema rel expandedOps with
        | Error dmErrors ->
          for e in dmErrors do
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: {e}"
        | Ok ops ->
          let selectWithArgNames =
            ops
            |> List.choose (function
              | Op.SelectWith args -> Some args
              | _ -> None)
            |> List.concat
            |> List.map (fun a -> a.ToLowerInvariant())
            |> Set.ofList

          let selectWithCount =
            ops
            |> List.filter (function
              | Op.SelectWith _ -> true
              | _ -> false)
            |> List.length

          if selectWithCount > 1 then
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: at most one select_with op is allowed on '{rel.name}'"
          else
            let filterSearchCount =
              ops
              |> List.filter (function
                | Op.FilterSearch _ -> true
                | _ -> false)
              |> List.length

            let filterCountOpCount = ops |> List.filter ((=) Op.FilterCount) |> List.length

            if filterSearchCount > 1 then
              errors.Add $"{ann.sourceFile}:{ann.sourceLine}: at most one filter_search op is allowed on '{rel.name}'"
            elif filterCountOpCount > 1 then
              errors.Add $"{ann.sourceFile}:{ann.sourceLine}: at most one filter_count op is allowed on '{rel.name}'"
            else
              match resolveFilters rel ann.filters with
              | Error filterErrors ->
                for e in filterErrors do
                  errors.Add $"{ann.sourceFile}:{ann.sourceLine}: {e}"
              | Ok resolvedFilters ->
                if (hasFilterSearch ops || hasFilterCount ops) && resolvedFilters.IsEmpty then
                  errors.Add
                    $"{ann.sourceFile}:{ann.sourceLine}: filter_search/filter_count require at least one -- mig:filter on '{rel.name}'"
                elif not (hasFilterSearch ops || hasFilterCount ops) && not resolvedFilters.IsEmpty then
                  errors.Add
                    $"{ann.sourceFile}:{ann.sourceLine}: -- mig:filter requires filter_search and/or filter_count on '{rel.name}'"
                else
                  let overrideMap =
                    System.Collections.Generic.Dictionary<string, ColumnOverrideKind>(StringComparer.OrdinalIgnoreCase)

                  let mutable overrideOk = true

                  for ov in ann.overrides do
                    match resolveColumnName rel ov.column with
                    | Some realName ->
                      if overrideMap.ContainsKey realName then
                        errors.Add $"{ann.sourceFile}:{ann.sourceLine}: duplicate override for column '{realName}'"
                        overrideOk <- false
                      else
                        overrideMap[realName] <- ov.kind
                    | None when selectWithArgNames.Contains(ov.column.ToLowerInvariant()) ->
                      if overrideMap.ContainsKey ov.column then
                        errors.Add $"{ann.sourceFile}:{ann.sourceLine}: duplicate override for '{ov.column}'"
                        overrideOk <- false
                      else
                        overrideMap[ov.column] <- ov.kind
                    | None ->
                      errors.Add
                        $"{ann.sourceFile}:{ann.sourceLine}: override column '{ov.column}' not found on '{rel.name}' (and not a select_with arg)"

                      overrideOk <- false

                  if overrideOk then
                    let fsName =
                      match ann.fsNameOverride with
                      | Some name -> sanitizeFsIdent name
                      | None -> sanitizeFsIdent (toPascalCase rel.name)

                    if not (usedFsNames.Add fsName) then
                      errors.Add $"{ann.sourceFile}:{ann.sourceLine}: duplicate F# relation name '{fsName}'"
                    else
                      let overrides = overrideMap |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

                      let selectWithPlanResult =
                        match
                          ops
                          |> List.tryPick (function
                            | Op.SelectWith args -> Some args
                            | _ -> None)
                        with
                        | None -> Ok None
                        | Some args ->
                          match ann.createSql with
                          | None
                          | Some "" ->
                            Error $"{ann.sourceFile}:{ann.sourceLine}: select_with requires CREATE VIEW source SQL"
                          | Some createSql ->
                            match SelectWith.buildPlan args createSql overrides with
                            | Error e -> Error $"{ann.sourceFile}:{ann.sourceLine}: {e}"
                            | Ok plan -> Ok(Some plan)

                      match selectWithPlanResult with
                      | Error e -> errors.Add e
                      | Ok selectWith ->
                        results.Add
                          { sqlName = rel.name
                            fsName = fsName
                            kind = rel.kind
                            columns = rel.columns
                            ops = ops
                            filters = resolvedFilters
                            overrides = overrides
                            selectWith = selectWith }

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
        | Some rel -> processAnnotation schema ann rel errors results usedFsNames
        | None ->
          let matchKey =
            schema.Keys
            |> Seq.tryFind (fun k -> k.Equals(sqlName, StringComparison.OrdinalIgnoreCase))

          match matchKey with
          | None ->
            errors.Add $"{ann.sourceFile}:{ann.sourceLine}: relation '{sqlName}' not found in schema after migrations"
          | Some key -> processAnnotation schema ann schema[key] errors results usedFsNames

    if errors.Count > 0 then
      Error(String.concat Environment.NewLine (errors |> Seq.toList))
    else
      Ok(results |> List.ofSeq)
