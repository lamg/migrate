# Claude Code Guidelines for migrate Project

## F# Coding Conventions

### Method Call Style - Single Argument Functions

When calling methods with a single argument, use function syntax instead of parenthesized syntax:

**Preferred:**
```fsharp
upper.Contains "NOT NULL"
str.StartsWith "CREATE"
Default <| Value ""
```

**Avoid:**
```fsharp
upper.Contains("NOT NULL")
str.StartsWith("CREATE")
Default(Value "")
```

This applies to all single-argument method calls and improves readability and follows idiomatic F# conventions.

### Related Methods
- `.Contains`
- `.StartsWith`
- `.EndsWith`
- `.Trim`
- `.ToUpper`
- `.ToLower`
- Any other single-argument method or function

### Module Declaration Style - Module Per File

When creating a file with a single module, use module-per-file syntax instead of namespace with nested module:

**Preferred:**
```fsharp
/// Documentation comment for the module
module migrate.Db

open System
open Microsoft.Data.Sqlite

let someFunction x = x + 1
```

**Avoid:**
```fsharp
namespace migrate

open System
open Microsoft.Data.Sqlite

module Db =
  let someFunction x = x + 1
```

This applies to all single-module files in the project and follows modern F# conventions for cleaner, less-indented code.

### Code Formatting

All F# code must be formatted using [Fantomas](https://fsprojects.github.io/fantomas/) before committing.

**Format all files in the project:**
```bash
cd src
dotnet fantomas .
```

**Format specific files:**
```bash
dotnet fantomas src/MigLib/Db.fs
```

This ensures consistent code style across the entire codebase and prevents formatting-related diffs in commits.

## Testing
All changes should pass the existing test suite:
```bash
cd src && dotnet test
```

Consider that print debugging with `dotnet test` is not longer possible in Dotnet 10 because a the test output is captured.

## Code Generation Conventions

### Generated Project Files

The `mig codegen` command generates `.fsproj` files that use **Central Package Management (CPM)**.

**Generated format:**
```xml
<ItemGroup>
  <PackageReference Include="FsToolkit.ErrorHandling" />
  <PackageReference Include="Microsoft.Data.Sqlite" />
  <PackageReference Include="MigLib" />
</ItemGroup>
```

**Why CPM:**
- Modern .NET best practice for managing package versions
- Centralized version control via `Directory.Packages.props`
- Easier to maintain consistent versions across multiple projects
- Reduces duplication in project files

**User setup requirement:**
Users must create a `Directory.Packages.props` file in their solution root:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="FsToolkit.ErrorHandling" Version="4.18.0" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="9.0.0" />
    <PackageVersion Include="MigLib" Version="2.0.0" />
  </ItemGroup>
</Project>
```

**Note:** This is a breaking change from traditional package references with inline versions. Users without CPM will get build error `NU1015` and must set up `Directory.Packages.props`.

## Version Management

### Version Synchronization

**CRITICAL:** MigLib and migtool (mig CLI) versions MUST be kept in sync to prevent version mismatch issues.

**Why this matters:**
- The mig CLI tool depends on MigLib via project reference
- Users install migtool from NuGet and expect all features in that version
- Code generation is implemented in MigLib but invoked via mig
- Version mismatch causes confusion about which features are available

**Process for version bumps:**

1. **When adding features to MigLib:**
   - Update version in `src/MigLib/MigLib.fsproj`
   - Update version in `src/mig/mig.fsproj` to match
   - Both versions must be identical

2. **When releasing:**
   - Verify both package versions match
   - Update CHANGELOG.md with changes for BOTH packages
   - Document which features were added to MigLib vs mig CLI

3. **Version files to check:**
   - `src/MigLib/MigLib.fsproj` (line 9: `<Version>X.Y.Z</Version>`)
   - `src/mig/mig.fsproj` (line 13: `<Version>X.Y.Z</Version>`)

**Example:**
If you add a new code generation feature (in MigLib) and a new CLI command (in mig):
- Bump BOTH to the same version (e.g., 2.3.0)
- Document the MigLib changes (code generation improvements)
- Document the mig changes (new CLI command)
- Ensure CHANGELOG reflects both packages at the same version

## Documentation Conventions

### Feature Documentation Structure

The project uses two main documentation files with distinct purposes:

#### specs

The directory contains design documents that can be used to derive the whole implementation with few sensible decisions.

#### PROGRESS.md - Current plan and its completion status

**Contains:**
- High-level status summary (completed tasks, current work)
- Implementation timeline and milestones
- Brief notes on what was implemented in each phase
- Future work and next steps
- Quick reference for resuming development

**When to update:**
- During active development to track progress
- When completing major milestones or phases
- When planning next steps
