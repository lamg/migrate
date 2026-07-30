namespace MigLib.Codegen

module internal Annotations =
  open System
  open System.IO
  open System.Text.RegularExpressions
  open MigLib.Codegen.Types

  let private migPrefix = "-- mig:"

  let private createRelationRegex =
    Regex(
      @"^\s*CREATE\s+(TABLE|VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:['""]?(\w+)['""]?|\[(\w+)\])",
      RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

  let private splitOps (body: string) =
    // Split on commas not inside parentheses.
    let acc = ResizeArray<string>()
    let sb = System.Text.StringBuilder()
    let mutable depth = 0

    for ch in body do
      match ch with
      | '(' ->
        depth <- depth + 1
        sb.Append ch |> ignore
      | ')' ->
        depth <- depth - 1
        sb.Append ch |> ignore
      | ',' when depth = 0 ->
        let part = sb.ToString().Trim()

        if part.Length > 0 then
          acc.Add part

        sb.Clear() |> ignore
      | _ -> sb.Append ch |> ignore

    let tail = sb.ToString().Trim()

    if tail.Length > 0 then
      acc.Add tail

    acc |> List.ofSeq

  let private parseOp (raw: string) : Result<Op, string> =
    let text = raw.Trim()
    let lower = text.ToLowerInvariant()

    let parseParenArgs (keyword: string) =
      let prefix = keyword + "("

      if lower.StartsWith prefix && text.EndsWith ")" then
        let inner = text.Substring(prefix.Length, text.Length - prefix.Length - 1)

        inner.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.toList
        |> Some
      else
        None

    match lower with
    | "insert" -> Ok Op.Insert
    | "insert_or_ignore" -> Ok Op.InsertOrIgnore
    | "insert_many" -> Ok Op.InsertMany
    | "upsert" -> Ok Op.Upsert
    | "upsert_many" -> Ok Op.UpsertMany
    | "select_all" -> Ok Op.SelectAll
    | "select_by_id" -> Ok Op.SelectById
    | "delete_by_id" -> Ok Op.DeleteById
    | "delete_all" -> Ok Op.DeleteAll
    | _ when lower.StartsWith "select_by_or_insert(" ->
      match parseParenArgs "select_by_or_insert" with
      | Some cols when cols.Length > 0 -> Ok(Op.SelectByOrInsert cols)
      | _ -> Error $"invalid select_by_or_insert op: {text}"
    | _ when lower.StartsWith "select_by(" ->
      match parseParenArgs "select_by" with
      | Some cols when cols.Length > 0 -> Ok(Op.SelectBy cols)
      | _ -> Error $"invalid select_by op: {text}"
    | _ when lower.StartsWith "select_one_by(" ->
      match parseParenArgs "select_one_by" with
      | Some cols when cols.Length > 0 -> Ok(Op.SelectOneBy cols)
      | _ -> Error $"invalid select_one_by op: {text}"
    | _ when lower.StartsWith "select_like(" ->
      match parseParenArgs "select_like" with
      | Some [ col ] -> Ok(Op.SelectLike col)
      | _ -> Error $"invalid select_like op: {text}"
    | _ when lower.StartsWith "select_top(" ->
      match parseParenArgs "select_top" with
      | Some [ col; n ] ->
        match Int32.TryParse n with
        | true, limit when limit > 0 -> Ok(Op.SelectTop(col, limit))
        | _ -> Error $"invalid select_top limit (must be positive int): {text}"
      | _ -> Error $"invalid select_top op (expected select_top(column, n)): {text}"
    | _ when lower.StartsWith "select_bottom(" ->
      match parseParenArgs "select_bottom" with
      | Some [ col; n ] ->
        match Int32.TryParse n with
        | true, limit when limit > 0 -> Ok(Op.SelectBottom(col, limit))
        | _ -> Error $"invalid select_bottom limit (must be positive int): {text}"
      | _ -> Error $"invalid select_bottom op (expected select_bottom(column, n)): {text}"
    | _ when lower.StartsWith "select_range(" ->
      let prefix = "select_range("

      if not (text.EndsWith ")") then
        Error $"invalid select_range op (expected select_range(col [asc|desc], ...)): {text}"
      else
        let inner = text.Substring(prefix.Length, text.Length - prefix.Length - 1).Trim()

        if String.IsNullOrWhiteSpace inner then
          Error $"invalid select_range op (at least one order column required): {text}"
        else
          // Split on commas only so "created_at desc" stays one segment.
          let segments =
            inner.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.toList

          let parseSegment (seg: string) =
            let tokens =
              seg.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
              |> Array.toList

            match tokens with
            | [ col ] -> Ok(col, SortDirection.Asc)
            | [ col; dir ] ->
              match dir.ToLowerInvariant() with
              | "asc" -> Ok(col, SortDirection.Asc)
              | "desc" -> Ok(col, SortDirection.Desc)
              | _ -> Error $"invalid select_range direction '{dir}' (expected asc|desc): {text}"
            | _ -> Error $"invalid select_range order key '{seg}' (expected col [asc|desc]): {text}"

          let parsed = segments |> List.map parseSegment

          let errors =
            parsed
            |> List.choose (function
              | Error e -> Some e
              | Ok _ -> None)

          if not errors.IsEmpty then
            Error(String.concat "; " errors)
          elif parsed.IsEmpty then
            Error $"invalid select_range op (at least one order column required): {text}"
          else
            let orderBy =
              parsed
              |> List.choose (function
                | Ok o -> Some o
                | Error _ -> None)

            Ok(Op.SelectRange orderBy)
    | _ when lower.StartsWith "select_with(" ->
      match parseParenArgs "select_with" with
      | Some args when args.Length > 0 -> Ok(Op.SelectWith args)
      | _ -> Error $"invalid select_with op (expected select_with(arg, ...)): {text}"
    | _ when lower.StartsWith "delete_by(" ->
      match parseParenArgs "delete_by" with
      | Some cols when cols.Length > 0 -> Ok(Op.DeleteBy cols)
      | _ -> Error $"invalid delete_by op: {text}"
    | _ -> Error $"unknown op: {text}"

  let private parseMigLine (line: string) : Result<Choice<string, Op list, ColumnOverride>, string> option =
    let trimmed = line.Trim()

    if not (trimmed.StartsWith migPrefix) then
      None
    else
      let body = trimmed.Substring(migPrefix.Length).Trim()
      let spaceIdx = body.IndexOf ' '

      let key, rest =
        if spaceIdx < 0 then
          body.ToLowerInvariant(), ""
        else
          body.Substring(0, spaceIdx).ToLowerInvariant(), body.Substring(spaceIdx + 1).Trim()

      match key with
      | "rel" ->
        if String.IsNullOrWhiteSpace rest then
          Some(Error "mig:rel requires a name")
        else
          Some(Ok(Choice1Of3 rest))
      | "ops" ->
        if String.IsNullOrWhiteSpace rest then
          Some(Error "mig:ops requires at least one op")
        else
          let parts = splitOps rest
          let parsed = parts |> List.map parseOp

          let errors =
            parsed
            |> List.choose (function
              | Error e -> Some e
              | Ok _ -> None)

          if not errors.IsEmpty then
            Some(Error(String.concat "; " errors))
          else
            let ops =
              parsed
              |> List.choose (function
                | Ok o -> Some o
                | Error _ -> None)

            Some(Ok(Choice2Of3 ops))
      | "bool"
      | "int"
      | "uint"
      | "int64"
      | "datetime" ->
        if String.IsNullOrWhiteSpace rest then
          Some(Error $"mig:{key} requires a column name")
        else
          let kind =
            match key with
            | "bool" -> ColumnOverrideKind.Bool
            | "int" -> ColumnOverrideKind.Int
            | "uint" -> ColumnOverrideKind.UInt
            | "int64" -> ColumnOverrideKind.Int64
            | "datetime" -> ColumnOverrideKind.DateTime
            | _ -> ColumnOverrideKind.Int64

          Some(Ok(Choice3Of3 { column = rest; kind = kind }))
      | _ -> Some(Error $"unknown mig annotation: {key}")

  let private extractRelationName (line: string) =
    let m = createRelationRegex.Match line

    if not m.Success then
      None
    else
      let kind =
        if m.Groups[1].Value.Equals("VIEW", StringComparison.OrdinalIgnoreCase) then
          RelationKind.View
        else
          RelationKind.Table

      let name =
        if m.Groups[2].Success then m.Groups[2].Value
        elif m.Groups[3].Success then m.Groups[3].Value
        else ""

      if String.IsNullOrWhiteSpace name then
        None
      else
        Some(kind, name)

  /// Scans migration SQL files for -- mig: annotation blocks associated with the next CREATE TABLE/VIEW.
  let parseMigrationsDirectory (migrationsDir: string) : Result<RelationAnnotation list, string> =
    try
      if not (Directory.Exists migrationsDir) then
        Error $"migrations directory not found: {migrationsDir}"
      else
        let files =
          Directory.GetFiles(migrationsDir, "*.sql")
          |> Array.sortBy (fun f -> f.ToLowerInvariant())
          |> Array.toList

        let annotations = ResizeArray<RelationAnnotation>()
        let mutable errors = []

        for file in files do
          let lines = File.ReadAllLines file
          let pending = ResizeArray<string * int * Choice<string, Op list, ColumnOverride>>()
          // each entry: raw already parsed as Choice

          let flushPending () = pending.Clear()

          let commitForRelation (sqlName: string) (lineNo: int) (createSql: string option) =
            if pending.Count = 0 then
              ()
            else
              let mutable fsName = None
              let ops = ResizeArray<Op>()
              let overrides = ResizeArray<ColumnOverride>()

              for _, _, item in pending do
                match item with
                | Choice1Of3 name -> fsName <- Some name
                | Choice2Of3 opList -> ops.AddRange opList
                | Choice3Of3 ov -> overrides.Add ov

              if ops.Count > 0 then
                annotations.Add
                  { sqlName = Some sqlName
                    fsNameOverride = fsName
                    ops = ops |> List.ofSeq
                    overrides = overrides |> List.ofSeq
                    sourceFile = file
                    sourceLine = lineNo
                    createSql = createSql }

              flushPending ()

          for i = 0 to lines.Length - 1 do
            let line = lines[i]
            let lineNo = i + 1

            match parseMigLine line with
            | Some(Error e) -> errors <- $"%s{file}:{lineNo}: {e}" :: errors
            | Some(Ok item) -> pending.Add(line, lineNo, item)
            | None ->
              match extractRelationName line with
              | Some(_, sqlName) ->
                let createSql =
                  if pending.Count > 0 then
                    Some(SelectWith.extractStatement lines i)
                  else
                    None

                commitForRelation sqlName lineNo createSql
              | None ->
                // Non-mig, non-create line: if we have pending annotations and hit blank-ish content that's not another mig,
                // keep pending until CREATE. Blank lines are fine.
                ()

          if pending.Count > 0 then
            errors <-
              $"%s{file}: dangling mig annotations without following CREATE TABLE/VIEW"
              :: errors

        if not errors.IsEmpty then
          Error(errors |> List.rev |> String.concat Environment.NewLine)
        else
          Ok(annotations |> List.ofSeq)
    with ex ->
      Error ex.Message
