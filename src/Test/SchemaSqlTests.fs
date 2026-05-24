module Test.SchemaSqlTests

open MigLib.Schema.Sql
open MigLib.Schema.Types
open Xunit

[<Fact>]
let ``quoteIdentifier escapes embedded double quotes`` () =
  Assert.Equal("\"some\"\"table\"", quoteIdentifier "some\"table")

[<Fact>]
let ``sqlTypeStorageName matches SQLite storage names`` () =
  Assert.Equal("INTEGER", sqlTypeStorageName SqlInteger)
  Assert.Equal("TEXT", sqlTypeStorageName SqlText)
  Assert.Equal("REAL", sqlTypeStorageName SqlReal)
  Assert.Equal("TEXT", sqlTypeStorageName SqlTimestamp)
  Assert.Equal("TEXT", sqlTypeStorageName SqlString)

[<Fact>]
let ``sqlTypeDisplayName preserves domain names for diagnostics`` () =
  Assert.Equal("INTEGER", sqlTypeDisplayName SqlInteger)
  Assert.Equal("TEXT", sqlTypeDisplayName SqlText)
  Assert.Equal("REAL", sqlTypeDisplayName SqlReal)
  Assert.Equal("TIMESTAMP", sqlTypeDisplayName SqlTimestamp)
  Assert.Equal("STRING", sqlTypeDisplayName SqlString)

[<Fact>]
let ``parseDeclaredSqlType maps SQLite declared types`` () =
  Assert.Equal(SqlInteger, parseDeclaredSqlType "INTEGER")
  Assert.Equal(SqlInteger, parseDeclaredSqlType "BIGINT")
  Assert.Equal(SqlText, parseDeclaredSqlType "VARCHAR(255)")
  Assert.Equal(SqlReal, parseDeclaredSqlType "DOUBLE")
  Assert.Equal(SqlTimestamp, parseDeclaredSqlType "TIMESTAMP")
  Assert.Equal(SqlString, parseDeclaredSqlType "BLOB")

[<Fact>]
let ``fkActionSql renders SQLite foreign key actions`` () =
  Assert.Equal("CASCADE", fkActionSql Cascade)
  Assert.Equal("RESTRICT", fkActionSql Restrict)
  Assert.Equal("NO ACTION", fkActionSql NoAction)
  Assert.Equal("SET NULL", fkActionSql SetNull)
  Assert.Equal("SET DEFAULT", fkActionSql SetDefault)
