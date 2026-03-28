module MigLib.Build

open System
open System.IO
open System.Reflection
open Mig.CodeGen.CodeGen

let deriveSchemaBoundDbFileName (schemaPath: string) : Result<string, string> =
  deriveDatabaseFileNameFromSourcePath schemaPath

let generateDbCodeFromTypes
  (moduleName: string)
  (schemaPath: string)
  (types: Type list)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  generateCodeFromTypesWithDbFile moduleName schemaPath types outputFilePath

let generateDbCodeFromAssemblyModule
  (generatedModuleName: string)
  (schemaPath: string)
  (assembly: Assembly)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  generateCodeFromAssemblyModuleWithDbFile generatedModuleName schemaPath assembly schemaModuleName outputFilePath

let generateDbCodeFromAssemblyModulePath
  (generatedModuleName: string)
  (schemaPath: string)
  (assemblyPath: string)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  if String.IsNullOrWhiteSpace assemblyPath then
    Error "Compiled assembly path is empty."
  else
    let fullAssemblyPath = Path.GetFullPath assemblyPath

    if not (File.Exists fullAssemblyPath) then
      Error $"Compiled assembly was not found: {fullAssemblyPath}"
    else
      try
        let assembly = Assembly.LoadFrom fullAssemblyPath
        generateDbCodeFromAssemblyModule generatedModuleName schemaPath assembly schemaModuleName outputFilePath
      with ex ->
        Error $"Could not load compiled assembly '{fullAssemblyPath}': {ex.Message}"
