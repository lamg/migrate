module MigLib.Generated

type SqlType = Schema.Types.SqlType
type EnumLikeDu = Schema.Types.EnumLikeDu
type Expr = Schema.Types.Expr
type InsertInto = Schema.Types.InsertInto
type FkAction = Schema.Types.FkAction
type ForeignKey = Schema.Types.ForeignKey
type PrimaryKey = Schema.Types.PrimaryKey
type ColumnConstraint = Schema.Types.ColumnConstraint
type ColumnDef = Schema.Types.ColumnDef
type ViewColumn = Schema.Types.ViewColumn
type QueryByAnnotation = Schema.Types.QueryByAnnotation
type QueryLikeAnnotation = Schema.Types.QueryLikeAnnotation
type QueryByOrCreateAnnotation = Schema.Types.QueryByOrCreateAnnotation
type SelectOneAnnotation = Schema.Types.SelectOneAnnotation
type InsertOrIgnoreAnnotation = Schema.Types.InsertOrIgnoreAnnotation
type DeleteAllAnnotation = Schema.Types.DeleteAllAnnotation
type UpsertAnnotation = Schema.Types.UpsertAnnotation
type CreateView = Schema.Types.CreateView
type CreateTable = Schema.Types.CreateTable
type CreateIndex = Schema.Types.CreateIndex
type CreateTrigger = Schema.Types.CreateTrigger
type SqlFile = Schema.Types.SqlFile
type ResolvedGeneratedSchemaModule = Types.ResolvedGeneratedSchemaModule

let SelectOneAnnotation = Schema.Types.SelectOneAnnotation
let InsertOrIgnoreAnnotation = Schema.Types.InsertOrIgnoreAnnotation
let DeleteAllAnnotation = Schema.Types.DeleteAllAnnotation
let UpsertAnnotation = Schema.Types.UpsertAnnotation

let querySingle = Codegen.Helpers.querySingle
let queryList = Codegen.Helpers.queryList
let querySingleOrInsert = Codegen.Helpers.querySingleOrInsert
let executeWrite = Codegen.Helpers.executeWrite
let executeWriteUnit = Codegen.Helpers.executeWriteUnit
let getLastInsertRowId = Codegen.Helpers.getLastInsertRowId
let executeInsert = Codegen.Helpers.executeInsert
let executeInsertOrIgnore = Codegen.Helpers.executeInsertOrIgnore
let sequenceUnitResults = Codegen.Helpers.sequenceUnitResults
let upsertByExisting = Codegen.Helpers.upsertByExisting

module Recording =
  let ensureWriteAllowed = Runtime.Recording.ensureWriteAllowed
  let recordInsert = Runtime.Recording.recordInsert
  let recordUpdate = Runtime.Recording.recordUpdate
  let recordDelete = Runtime.Recording.recordDelete
