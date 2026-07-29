namespace MigLib.Codegen

module internal Naming =
  open System
  open System.Text

  let private isIdentStart (c: char) = Char.IsLetter c || c = '_'
  let private isIdentPart (c: char) = Char.IsLetterOrDigit c || c = '_'

  /// snake_case / mixed SQL identifier → PascalCase F# name.
  let toPascalCase (name: string) =
    if String.IsNullOrWhiteSpace name then
      "Relation"
    else
      let parts = name.Split([| '_'; '-'; ' ' |], StringSplitOptions.RemoveEmptyEntries)

      let sb = StringBuilder()

      for part in parts do
        if part.Length > 0 then
          sb.Append(Char.ToUpperInvariant part[0]) |> ignore

          if part.Length > 1 then
            sb.Append(part.Substring 1) |> ignore

      let result = sb.ToString()

      if result.Length = 0 then "Relation"
      elif Char.IsDigit result[0] then "R" + result
      else result

  let toCamelCase (name: string) =
    let pascal = toPascalCase name

    if pascal.Length = 0 then
      "value"
    else
      let head = string (Char.ToLowerInvariant pascal[0])
      let tail = if pascal.Length > 1 then pascal.Substring 1 else ""
      head + tail

  let sanitizeFsIdent (name: string) =
    let pascal = toPascalCase name

    match pascal with
    | "type"
    | "module"
    | "namespace"
    | "open"
    | "let"
    | "rec"
    | "and"
    | "match"
    | "with"
    | "function"
    | "fun"
    | "if"
    | "then"
    | "else"
    | "elif"
    | "for"
    | "while"
    | "do"
    | "done"
    | "in"
    | "to"
    | "downto"
    | "yield"
    | "return"
    | "use"
    | "try"
    | "finally"
    | "with"
    | "new"
    | "null"
    | "true"
    | "false"
    | "base"
    | "begin"
    | "end"
    | "as"
    | "assert"
    | "inline"
    | "lazy"
    | "mutable"
    | "of"
    | "exception"
    | "extern"
    | "interface"
    | "member"
    | "static"
    | "abstract"
    | "override"
    | "default"
    | "class"
    | "struct"
    | "enum"
    | "delegate"
    | "inherit"
    | "val"
    | "public"
    | "private"
    | "internal"
    | "global"
    | "const"
    | "when"
    | "select"
    | "from"
    | "where"
    | "order"
    | "group"
    | "by"
    | "join"
    | "on"
    | "into"
    | "yield!"
    | "return!" -> pascal + "_"
    | _ -> pascal

  let quoteSqlIdent (name: string) =
    // Square brackets avoid noisy escaping in generated F# string literals.
    "[" + name.Replace("]", "]]") + "]"

  let paramName (column: string) = "@" + column

  let selectByMemberName (columns: string list) =
    let suffix = columns |> List.map toPascalCase |> String.concat ""

    "selectBy" + suffix

  let selectOneByMemberName (columns: string list) =
    let suffix = columns |> List.map toPascalCase |> String.concat ""

    "selectOneBy" + suffix

  let deleteByMemberName (columns: string list) =
    let suffix = columns |> List.map toPascalCase |> String.concat ""

    "deleteBy" + suffix

  let selectLikeMemberName (column: string) = "select" + toPascalCase column + "Like"

  let selectTopMemberName (column: string) (limit: int) =
    $"selectTop{toPascalCase column}{limit}"

  let selectBottomMemberName (column: string) (limit: int) =
    $"selectBottom{toPascalCase column}{limit}"
