namespace Mig

open System
open System.Globalization
open Microsoft.FSharp.Reflection
open DeclarativeMigrations.Types

module internal SchemaReflectionNaming =
  let toSnakeCase (value: string) : string =
    if String.IsNullOrWhiteSpace value then
      value
    else
      let chars = Text.StringBuilder()

      for i in 0 .. value.Length - 1 do
        let c = value.[i]
        let hasPrev = i > 0
        let hasNext = i < value.Length - 1
        let prev = if hasPrev then value.[i - 1] else '\000'
        let next = if hasNext then value.[i + 1] else '\000'

        if
          hasPrev
          && Char.IsUpper c
          && (Char.IsLower prev || Char.IsUpper prev && hasNext && Char.IsLower next)
        then
          chars.Append '_' |> ignore

        chars.Append(Char.ToLowerInvariant c) |> ignore

      chars.ToString()

  let toCamelCaseFromSnake (value: string) : string =
    if String.IsNullOrWhiteSpace value then
      value
    else
      let parts = value.Split '_'

      if parts.Length = 0 then
        value
      else
        let head = parts.[0]

        let tail =
          parts
          |> Array.skip 1
          |> Array.map (fun part ->
            if String.IsNullOrWhiteSpace part then
              part
            else
              string (Char.ToUpperInvariant part.[0]) + part.[1..])

        head + String.Concat tail

  let isRecordType (t: Type) =
    try
      FSharpType.IsRecord(t, true)
    with _ ->
      false

  let isUnionType (t: Type) =
    try
      FSharpType.IsUnion(t, true)
    with _ ->
      false

  let tryGetEnumLikeDu (t: Type) : EnumLikeDu option =
    if t.ContainsGenericParameters || not (isUnionType t) then
      None
    else
      let unionCases = FSharpType.GetUnionCases(t, true) |> Array.toList

      if
        unionCases.IsEmpty
        || unionCases |> List.exists (fun unionCase -> unionCase.GetFields().Length > 0)
      then
        None
      else
        Some
          { typeName = t.Name
            cases = unionCases |> List.map _.Name }

  let mapSupportedScalarType (t: Type) : (SqlType * EnumLikeDu option) option =
    if t = typeof<int64> then
      Some(SqlInteger, None)
    elif t = typeof<string> then
      Some(SqlText, None)
    elif t = typeof<float> then
      Some(SqlReal, None)
    else
      tryGetEnumLikeDu t |> Option.map (fun enumLikeDu -> SqlText, Some enumLikeDu)

  let mapPrimitiveSqlType (t: Type) : SqlType option =
    mapSupportedScalarType t |> Option.map fst

  let parseDefaultLiteral (value: string) : Expr =
    let trimmed = value.Trim()

    let parsedInt, intValue =
      Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture)

    if parsedInt then
      Integer intValue
    else
      let parsedFloat, floatValue =
        Double.TryParse(trimmed, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.InvariantCulture)

      if parsedFloat then
        Real floatValue
      elif trimmed.StartsWith "'" && trimmed.EndsWith "'" && trimmed.Length >= 2 then
        String(trimmed.[1 .. trimmed.Length - 2])
      else
        Value trimmed
