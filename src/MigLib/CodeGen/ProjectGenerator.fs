module internal migrate.CodeGen.ProjectGenerator

open System.IO

/// Generate .fsproj file content
let generateProjectFile (projectName: string) (sourceFiles: string list) : string =
  let compileIncludes =
    sourceFiles
    |> List.map (fun file ->
      let fileName = Path.GetFileName file
      $"""    <Compile Include="{fileName}" />""")
    |> String.concat "\n"

  $"""<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
{compileIncludes}
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="FSharp.Core" Version="10.0.100" />
    <PackageReference Include="FsToolkit.ErrorHandling" Version="4.18.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
  </ItemGroup>

</Project>
"""

/// Write project file to disk
let writeProjectFile (directory: string) (projectName: string) (sourceFiles: string list) =
  let projectPath = Path.Combine(directory, $"{projectName}.fsproj")
  let content = generateProjectFile projectName sourceFiles
  File.WriteAllText(projectPath, content)
  projectPath
