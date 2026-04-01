namespace Mig

open DeclarativeMigrations.Types

type internal PrimaryKeyInfo =
  { columnName: string
    sqlType: SqlType
    isAutoIncrement: bool }

type internal ViewJoin =
  { leftTable: string
    rightTable: string }
