namespace MigLib.Resolution.SchemaReflection

open MigLib.Schema.Types

type internal PrimaryKeyInfo =
  { columnName: string
    sqlType: SqlType
    isAutoIncrement: bool }

type internal ViewJoin =
  { leftTable: string
    rightTable: string }
