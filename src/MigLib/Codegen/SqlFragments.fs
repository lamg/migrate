module internal MigLib.Codegen.SqlFragments

let appendOrderBy (orderBy: string option) (sql: string) : string =
  match orderBy with
  | Some orderBy when not (System.String.IsNullOrWhiteSpace orderBy) -> $"{sql} ORDER BY {orderBy}"
  | _ -> sql
