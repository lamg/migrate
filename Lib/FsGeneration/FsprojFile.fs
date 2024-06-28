// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module internal Migrate.FsGeneration.FsprojFile

open System.IO
open System.Xml.Linq
open Migrate.Types

let projectToFsproj (p: Project) =

  let includeQuery =
    XElement(XName.Get "Compile", XAttribute(XName.Get "Include", "Query.fs"))

  let fs =
    p.includeFsFiles
    |> List.map (fun f -> XElement(XName.Get "Compile", XAttribute(XName.Get "Include", f)))

  XElement(
    XName.Get "Project",
    XAttribute(XName.Get "Sdk", "Microsoft.NET.Sdk"),

    XElement(XName.Get "PropertyGroup", XElement(XName.Get "TargetFramework", "net8.0")),

    XElement(XName.Get "ItemGroup", includeQuery :: fs),

    XElement(
      XName.Get "ItemGroup",
      XElement(
        XName.Get "PackageReference",
        XAttribute(XName.Get "Include", "Microsoft.Data.Sqlite"),
        XAttribute(XName.Get "Version", "8.0.6")
      ),
      XElement(
        XName.Get "PackageReference",
        XAttribute(XName.Get "Include", "MigrateLib"),
        XAttribute(XName.Get "Version", "0.0.19")
      )
    )
  )

let saveXmlTo (dir: string) (xml: XElement) =
  Path.Join(dir, "Database.fsproj") |> xml.Save
