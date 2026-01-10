module internal migrate.CodeGen.FileMapper

open System.IO

/// Convert SQL file name to F# module name
/// Example: students.sql -> Students
let sqlFileToModuleName (sqlFilePath: string) : string =
  let fileName = Path.GetFileNameWithoutExtension sqlFilePath

  // Capitalize first letter
  if String.length fileName > 0 then
    (string fileName.[0]).ToUpper() + fileName.[1..]
  else
    fileName

/// Convert SQL file name to F# file name
/// Example: students.sql -> Students.fs
let sqlFileToFSharpFile (sqlFilePath: string) : string =
  let directory = Path.GetDirectoryName sqlFilePath
  let moduleName = sqlFileToModuleName sqlFilePath
  Path.Combine(directory, $"{moduleName}.fs")

/// Ensure the output directory exists
let ensureDirectory (filePath: string) =
  let directory = Path.GetDirectoryName filePath

  if not (Directory.Exists directory) then
    Directory.CreateDirectory directory |> ignore
