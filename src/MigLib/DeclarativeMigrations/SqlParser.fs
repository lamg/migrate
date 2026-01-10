module internal migrate.DeclarativeMigrations.SqlParser

open System
open System.Text.RegularExpressions
open Types

// SQL parser using FParsec for robust parsing

let extractViewDependencies (sqlTokens: string seq) =
  let sql = String.concat "" sqlTokens

  let fromMatch =
    Regex.Match(sql, "FROM\s+(.+?)(?:WHERE|GROUP|ORDER|LIMIT|;|$)", RegexOptions.IgnoreCase)

  if fromMatch.Success then
    let fromClause = fromMatch.Groups.[1].Value

    fromClause.Split([| ','; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter (fun t -> not (String.IsNullOrWhiteSpace t))
    |> Array.toList
  else
    []

let prostProcViews (sql: string) (file: SqlFile) =
  let extractViewTokens view =
    let start = sql.IndexOf($"CREATE VIEW {view}", StringComparison.OrdinalIgnoreCase)

    let startAlt =
      sql.IndexOf($"CREATE TEMPORARY VIEW {view}", StringComparison.OrdinalIgnoreCase)

    let actualStart =
      if start >= 0 then start
      else if startAlt >= 0 then startAlt
      else -1

    if actualStart >= 0 then
      let viewStr =
        sql |> Seq.skip actualStart |> Seq.takeWhile (fun c -> c <> ';') |> Array.ofSeq

      let r = (new string (viewStr)).Trim()
      [ r ]
    else
      []

  let views =
    file.views
    |> List.map (fun v ->
      let tokens = v.name |> extractViewTokens
      let deps = extractViewDependencies tokens

      { v with
          sqlTokens = tokens
          dependencies = deps })

  { file with views = views }

let parse (file: string, sql: string) =
  // Use FParsec parser instead of regex-based parser
  match FParsecSqlParser.parseSqlFile (file, sql) with
  | Result.Ok parsed ->
    // Post-process views to extract dependencies
    parsed |> prostProcViews sql |> Result.Ok
  | Result.Error err ->
    Result.Error err
