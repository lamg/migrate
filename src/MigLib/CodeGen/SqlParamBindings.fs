module internal Mig.CodeGen.SqlParamBindings

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.ViewIntrospection

let addParamBinding (cmdVarName: string) (paramName: string) (valueExpr: string) =
  $"{cmdVarName}.Parameters.AddWithValue(\"@{paramName}\", {valueExpr}) |> ignore"

let addWrappedParamBinding (cmdVarName: string) (paramName: string) (valueExpr: string) =
  $"{cmdVarName}.Parameters.AddWithValue(\"@{paramName}\", ({valueExpr})) |> ignore"

let addColumnBinding (cmdVarName: string) (column: ColumnDef) (valueExpr: string) =
  if TypeGenerator.isColumnNullable column then
    addWrappedParamBinding cmdVarName column.name (TypeGenerator.toNullableDbValueExpr column valueExpr)
  else
    addParamBinding cmdVarName column.name (TypeGenerator.toDbValueExpr column valueExpr)

let addViewBinding (cmdVarName: string) (column: ViewColumn) (valueExpr: string) =
  addParamBinding cmdVarName column.name (TypeGenerator.toViewDbValueExpr column valueExpr)

let addPlainBinding (cmdVarName: string) (paramName: string) (valueExpr: string) =
  addParamBinding cmdVarName paramName valueExpr

let addOptionalPlainBinding (cmdVarName: string) (paramName: string) (valueExpr: string) =
  addWrappedParamBinding cmdVarName paramName $"match {valueExpr} with Some v -> box v | None -> box DBNull.Value"

let joinBindings (lineIndent: string) (bindings: string list) =
  String.concat $"\n{lineIndent}" bindings
