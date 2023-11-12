module internal Migrate.SqlGeneration.DropTable
open SqlParser.Types
let sqlDropTable (table: CreateTable) = $"DROP TABLE {table.name}"