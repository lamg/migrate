package declarative_migrations

import (
	"github.com/antlr4-go/antlr/v4"
	sqlite_parser "github.com/lamg/migrate/internal/antlr4_sqlite_parser"
)

type SqlVisitor struct {
	*sqlite_parser.BaseSQLiteParserVisitor
}

func (v *SqlVisitor) VisitSql_stmt_list() {

}

func (v *SqlVisitor) VisitCreate_table_stmt() {

}

func (v *SqlVisitor) VisitCreate_view_stmt() {

}

func (v *SqlVisitor) VisitCreate_index_stmt() {

}

func (v *SqlVisitor) VisitCreate_trigger_stmt() {

}

func parse(sql string) SqlFile {
	input := antlr.NewInputStream(sql)
	lexer := sqlite_parser.NewSQLiteLexer(input)
	stream := antlr.NewCommonTokenStream(lexer, antlr.TokenDefaultChannel)
	parser := sqlite_parser.NewSQLiteParser(stream)
	parser.BuildParseTrees = true
	ctx := parser.Parse()
	visitor := SqlVisitor{BaseSQLiteParserVisitor: &sqlite_parser.BaseSQLiteParserVisitor{}}
	chl := ctx.GetChildren()
	statList := visitor.Visit(chl[0])
	emptyFile := SqlFile{
		inserts:  []InsertInto{},
		views:    []CreateView{},
		tables:   []CreateTable{},
		indexes:  []CreateIndex{},
		triggers: []CreateTrigger{},
	}

	for _, s := range statList {

	}
}
