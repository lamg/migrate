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

#### spec.md - Specification and Feature Documentation
**Purpose:** Complete specification of the project including all implemented and planned features

**Contains:**
- Comprehensive overview of the project
- Complete documentation of all implemented features with examples
- Architecture details and design decisions
- API documentation and usage examples
- Testing strategy and current test counts
- Future enhancements and planned features

**When to update:**
- When a feature is fully implemented and tested
- When architecture or design decisions are finalized
- When API or usage patterns change
- When test coverage changes significantly

#### PROGRESS.md - Development History and Current Status
**Purpose:** Lightweight record of development progress and implementation history

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

### Documentation Workflow

1. **During Development:**
   - Track progress in PROGRESS.md with status updates
   - Document implementation decisions and challenges
   - Maintain lightweight notes about what was done

2. **After Feature Completion:**
   - Move complete feature documentation to spec.md
   - Include usage examples, API documentation, and behavior description
   - Update test counts and status in spec.md
   - Simplify PROGRESS.md entry to reference spec.md for details

3. **Long-term:**
   - spec.md serves as the authoritative documentation
   - PROGRESS.md provides historical context for development
   - Both files complement each other but avoid duplication

### Example

**In spec.md:**
```markdown
### 8. Normalized Schema Representation with Discriminated Unions

For normalized database schemas (2NF) that eliminate NULLs...

**Schema Example:**
[full examples with code]

**Generated Code:**
[complete generated code examples]

**Implementation Status:**
✅ COMPLETE - Fully implemented and integrated (January 2025)
[detailed feature list]
```

**In PROGRESS.md:**
```markdown
## ✅ Feature Complete: Normalized Schema Representation

**Status:** ✅ COMPLETE - All 7 phases implemented

**Progress Summary:**
- ✅ Phase 1: Detection and Validation (11 tests)
- ✅ Phase 2: Type Generation (8 tests)
[brief phase list]

See spec.md section 8 for complete documentation and usage examples.
```
