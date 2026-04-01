module internal Mig.CodeGen.QueryGenerator

let getPrimaryKey = QueryGeneratorCommon.getPrimaryKey
let getForeignKeys = QueryGeneratorCommon.getForeignKeys
let generateTableCode = QueryGeneratorTableGenerate.generateTableCode
let generateViewCode = QueryGeneratorViewGenerate.generateViewCode
