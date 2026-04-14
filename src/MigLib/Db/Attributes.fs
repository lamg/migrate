namespace MigLib

open System

module DbAttributes =
  [<Literal>]
  let Rfc3339UtcNow = "strftime('%Y-%m-%dT%H:%M:%SZ', 'now', 'utc')"

  [<AttributeUsage(AttributeTargets.Class)>]
  type AutoIncPKAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type PKAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type UniqueAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type DefaultAttribute(column: string, value: string) =
    inherit Attribute()
    member _.Column = column
    member _.Value = value

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type DefaultExprAttribute(column: string, expr: string) =
    inherit Attribute()
    member _.Column = column
    member _.Expr = expr

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type IndexAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns

  [<AttributeUsage(AttributeTargets.Class)>]
  type SelectAllAttribute() =
    inherit Attribute()
    member val OrderBy: string = null with get, set

  [<AttributeUsage(AttributeTargets.Class)>]
  type SelectOneAttribute() =
    inherit Attribute()
    member val OrderBy: string = null with get, set

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type SelectByAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns
    member val OrderBy: string = null with get, set

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type SelectOneByAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns
    member val OrderBy: string = null with get, set

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type SelectLikeAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type SelectByOrInsertAttribute([<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.Columns = columns

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type UpdateByAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type DeleteByAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class)>]
  type DeleteAllAttribute() =
    inherit Attribute()

  [<AttributeUsage(AttributeTargets.Class)>]
  type InsertOrIgnoreAttribute() =
    inherit Attribute()

  [<AttributeUsage(AttributeTargets.Class)>]
  type UpsertAttribute() =
    inherit Attribute()

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type FKAttribute(refTable: string, [<ParamArray>] columns: string array) =
    inherit Attribute()
    member _.RefTable = refTable
    member _.Columns = columns
    member val RefColumns: string array = [||] with get, set

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type OnDeleteCascadeAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type OnDeleteSetNullAttribute(column: string) =
    inherit Attribute()
    member _.Column = column

  [<AttributeUsage(AttributeTargets.Class)>]
  type ViewAttribute() =
    inherit Attribute()

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type JoinAttribute(left: Type, right: Type) =
    inherit Attribute()
    member _.Left = left
    member _.Right = right

  [<AttributeUsage(AttributeTargets.Class)>]
  type ViewSqlAttribute(sql: string) =
    inherit Attribute()
    member _.Sql = sql

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type OrderByAttribute(columns: string) =
    inherit Attribute()
    member _.Columns = columns

  [<AttributeUsage(AttributeTargets.Class ||| AttributeTargets.Property, AllowMultiple = false)>]
  type PreviousNameAttribute(name: string) =
    inherit Attribute()
    member _.Name = name

  [<AttributeUsage(AttributeTargets.Class, AllowMultiple = true)>]
  type DropColumnAttribute(name: string) =
    inherit Attribute()
    member _.Name = name
