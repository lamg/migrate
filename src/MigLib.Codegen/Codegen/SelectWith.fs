namespace MigLib.Codegen

/// Parse and rewrite /*@name*/literal markers in view SELECT bodies for select_with.
module internal SelectWith =
  open System
  open System.Text
  open System.Text.RegularExpressions
  open MigLib.Codegen.Types

  [<RequireQualifiedAccess>]
  type LiteralKind =
    | Integer
    | Real
    | String

  type Marker =
    { name: string
      literal: string
      kind: LiteralKind }

  let private markerRegex =
    Regex(@"/\*@([A-Za-z_][A-Za-z0-9_]*)\*/\s*(-?\d+\.\d+|-?\d+|'(?:[^']|'')*')", RegexOptions.Compiled)

  let private viewAsRegex =
    Regex(
      @"CREATE\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\[[^\]]+\]|""[^""]+""|'[^']+'|\w+)\s+AS\s+",
      RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

  let private classifyLiteral (lit: string) =
    if lit.StartsWith "'" then LiteralKind.String
    elif lit.Contains '.' then LiteralKind.Real
    else LiteralKind.Integer

  /// Find all /*@name*/literal markers in SQL text.
  let findMarkers (sql: string) : Result<Marker list, string> =
    let matches = markerRegex.Matches sql
    let acc = ResizeArray<Marker>()

    for m in matches do
      let name = m.Groups[1].Value
      let lit = m.Groups[2].Value

      acc.Add
        { name = name
          literal = lit
          kind = classifyLiteral lit }

    // Detect incomplete markers: /*@name*/ not followed by a valid literal.
    let loose = Regex.Matches(sql, @"/\*@([A-Za-z_][A-Za-z0-9_]*)\*/")

    if loose.Count <> matches.Count then
      Error "select_with marker must be followed by a scalar literal (int, real, or 'string')"
    else
      Ok(acc |> List.ofSeq)

  /// Replace each /*@name*/literal with @name.
  let rewriteMarkers (sql: string) : Result<string * Marker list, string> =
    match findMarkers sql with
    | Error e -> Error e
    | Ok markers ->
      let rewritten = markerRegex.Replace(sql, fun m -> "@" + m.Groups[1].Value)
      Ok(rewritten, markers)

  /// From a full CREATE VIEW statement, return the SELECT body (no trailing semicolon).
  let extractViewSelectSql (createSql: string) : Result<string, string> =
    let text = createSql.Trim()
    let m = viewAsRegex.Match text

    if not m.Success then
      Error "could not parse CREATE VIEW ... AS for select_with"
    else
      let body = text.Substring(m.Index + m.Length).Trim()

      let body =
        if body.EndsWith ";" then
          body.Substring(0, body.Length - 1).TrimEnd()
        else
          body

      if String.IsNullOrWhiteSpace body then
        Error "CREATE VIEW has empty SELECT body"
      else
        Ok body

  let literalKindToArgType (kind: LiteralKind) =
    match kind with
    | LiteralKind.Integer -> SelectWithArgType.Int64
    | LiteralKind.Real -> SelectWithArgType.Float
    | LiteralKind.String -> SelectWithArgType.String

  let overrideKindToArgType (kind: ColumnOverrideKind) =
    match kind with
    | ColumnOverrideKind.Bool -> SelectWithArgType.Bool
    | ColumnOverrideKind.Int -> SelectWithArgType.Int
    | ColumnOverrideKind.UInt -> SelectWithArgType.UInt
    | ColumnOverrideKind.Int64 -> SelectWithArgType.Int64
    | ColumnOverrideKind.DateTime -> SelectWithArgType.DateTime

  /// Build a select_with plan: typed args in declaration order + rewritten SQL.
  let buildPlan
    (declaredArgs: string list)
    (createSql: string)
    (overrides: Map<string, ColumnOverrideKind>)
    : Result<SelectWithPlan, string> =
    if declaredArgs.IsEmpty then
      Error "select_with requires at least one argument"
    else
      let dupes =
        declaredArgs
        |> List.groupBy (fun a -> a.ToLowerInvariant())
        |> List.choose (fun (_, g) -> if g.Length > 1 then Some g.Head else None)

      if not dupes.IsEmpty then
        let dupeList = String.concat ", " dupes
        Error $"select_with has duplicate argument name(s): {dupeList}"
      else
        match extractViewSelectSql createSql with
        | Error e -> Error e
        | Ok selectSql ->
          match rewriteMarkers selectSql with
          | Error e -> Error e
          | Ok(rewritten, markers) ->
            let markerNames =
              markers |> List.map _.name |> List.distinctBy (fun n -> n.ToLowerInvariant())

            let declaredSet =
              declaredArgs |> List.map (fun a -> a.ToLowerInvariant()) |> Set.ofList

            let markerSet =
              markerNames |> List.map (fun n -> n.ToLowerInvariant()) |> Set.ofList

            let missing =
              declaredArgs
              |> List.filter (fun a -> not (markerSet.Contains(a.ToLowerInvariant())))

            let unknown =
              markerNames
              |> List.filter (fun n -> not (declaredSet.Contains(n.ToLowerInvariant())))

            if not missing.IsEmpty then
              let missingList = String.concat ", " missing
              Error $"select_with argument(s) missing from view markers: {missingList}"
            elif not unknown.IsEmpty then
              let unknownList = String.concat ", " unknown
              Error $"view has select_with marker(s) not declared in select_with(...): {unknownList}"
            else
              // Prefer first marker occurrence for literal-kind inference per name.
              let kindByName =
                markers
                |> List.fold
                  (fun (acc: Map<string, LiteralKind>) m ->
                    let key = m.name.ToLowerInvariant()

                    if acc.ContainsKey key then acc else acc.Add(key, m.kind))
                  Map.empty

              let resolveName (declared: string) =
                markers
                |> List.tryFind (fun m -> m.name.Equals(declared, StringComparison.OrdinalIgnoreCase))
                |> Option.map _.name
                |> Option.defaultValue declared

              let args =
                declaredArgs
                |> List.map (fun declared ->
                  let name = resolveName declared
                  let key = name.ToLowerInvariant()

                  let argType =
                    match overrides |> Map.tryFind key with
                    | Some ov -> overrideKindToArgType ov
                    | None ->
                      // Also try exact key as stored in overrides map (original casing keys).
                      match
                        overrides
                        |> Map.tryPick (fun k v ->
                          if k.Equals(name, StringComparison.OrdinalIgnoreCase) then
                            Some v
                          else
                            None)
                      with
                      | Some ov -> overrideKindToArgType ov
                      | None -> literalKindToArgType kindByName[key]

                  { name = name; argType = argType })

              Ok { args = args; sql = rewritten }

  /// Collect a SQL statement starting at startIndex until a top-level semicolon.
  let extractStatement (lines: string[]) (startIndex: int) : string =
    let sb = StringBuilder()
    let mutable i = startIndex
    let mutable inSingle = false
    let mutable inLineComment = false
    let mutable inBlockComment = false
    let mutable finished = false

    while i < lines.Length && not finished do
      let line = lines[i]

      if i > startIndex then
        sb.AppendLine() |> ignore

      let mutable j = 0

      while j < line.Length && not finished do
        let c = line[j]
        let next = if j + 1 < line.Length then line[j + 1] else '\000'

        if inLineComment then
          sb.Append c |> ignore
          j <- j + 1
        elif inBlockComment then
          sb.Append c |> ignore

          if c = '*' && next = '/' then
            sb.Append next |> ignore
            j <- j + 2
            inBlockComment <- false
          else
            j <- j + 1
        elif inSingle then
          sb.Append c |> ignore

          if c = '\'' then
            if next = '\'' then
              sb.Append next |> ignore
              j <- j + 2
            else
              inSingle <- false
              j <- j + 1
          else
            j <- j + 1
        elif c = '-' && next = '-' then
          inLineComment <- true
          sb.Append c |> ignore
          j <- j + 1
        elif c = '/' && next = '*' then
          inBlockComment <- true
          sb.Append c |> ignore
          j <- j + 1
        elif c = '\'' then
          inSingle <- true
          sb.Append c |> ignore
          j <- j + 1
        elif c = ';' then
          sb.Append c |> ignore
          finished <- true
          j <- j + 1
        else
          sb.Append c |> ignore
          j <- j + 1

      inLineComment <- false
      i <- i + 1

    sb.ToString().Trim()
