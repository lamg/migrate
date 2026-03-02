---
name: migrate-codegen-cpm
description: Code generation conventions for `mig codegen` project files. Use when generating or reviewing `.fsproj` output and package references to ensure Central Package Management (CPM) compatibility.
---

# Migrate Codegen CPM

## Generate CPM-style package references

- Keep generated `.fsproj` package references versionless.
- Generate entries like:

```xml
<ItemGroup>
  <PackageReference Include="FsToolkit.ErrorHandling" />
  <PackageReference Include="Microsoft.Data.Sqlite" />
  <PackageReference Include="MigLib" />
</ItemGroup>
```

## Require Directory.Packages.props

- Expect users to manage versions in a solution-level `Directory.Packages.props`.
- Ensure users set `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`.
- Document that missing CPM setup causes `NU1015`.

## Rationale

- Keep package versions centralized and consistent across projects.
- Reduce inline duplication in generated `.fsproj` files.
