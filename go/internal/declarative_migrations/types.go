package declarative_migrations

type case_t int

const (
	INTEGER_TYPE case_t = iota
	TEXT_TYPE
	REAL_TYPE
	TIMESTAMP_TYPE
	STRING_TYPE
	FLEXIBLE_TYPE
)

type SqlType struct {
	case_t case_t
}

func IntegerType() SqlType {
	return SqlType{case_t: INTEGER_TYPE}
}

func TextType() SqlType {
	return SqlType{case_t: TEXT_TYPE}
}

func RealType() SqlType {
	return SqlType{case_t: REAL_TYPE}
}

func TimestampType() SqlType {
	return SqlType{case_t: TIMESTAMP_TYPE}
}

func StringType() SqlType {
	return SqlType{case_t: STRING_TYPE}
}

func FlexibleType() SqlType {
	return SqlType{case_t: FLEXIBLE_TYPE}
}

type case_expr = int

const (
	STRING_EXPR case_expr = iota
	INTEGER_EXPR
	REAL_EXPR
	VALUE_EXPR
)

type Expr struct {
	case_expr case_expr
}

func StringExpr() Expr {
	return Expr{case_expr: STRING_EXPR}
}

func IntegerExpr() Expr {
	return Expr{case_expr: INTEGER_EXPR}
}

func RealExpr() Expr {
	return Expr{case_expr: REAL_EXPR}
}

func ValueExpr() Expr {
	return Expr{case_expr: VALUE_EXPR}
}

type InsertInto struct {
	table   string
	columns []string
	values  [][]Expr
}

type ForeignKey struct {
	columns    []string
	refTable   string
	refColumns []string
}

type Option[T any] struct {
	value T
	set   bool
}

func Some[T any](value T) Option[T] {
	return Option[T]{
		value: value,
		set:   true,
	}
}

func None[T any]() Option[T] {
	return Option[T]{
		set: false,
	}
}

type PrimaryKey struct {
	constraintName  Option[string]
	columns         []string
	isAutoincrement bool
}

type ColumnConstraint struct {
	primaryKey      Option[PrimaryKey]
	isAutoIncrement bool
	isNotNull       bool
	unique          Option[[]string]
	defaultValue    Option[[]string]
	check           Option[[]string]
	foreignKey      Option[ForeignKey]
}

type ColumnDef struct {
	name        string
	columnType  SqlType
	constraints []ColumnConstraint
}

type CreateView struct {
	name         string
	sqlTokens    []string
	dependencies []string
}

type CreateTable struct {
	name        string
	columns     []ColumnDef
	constraints []ColumnConstraint
}

type CreateIndex struct {
	name    string
	table   string
	columns []string
}

type CreateTrigger struct {
	name         string
	sqlTokens    []string
	dependencies []string
}

type SqlFile struct {
	inserts  []InsertInto
	views    []CreateView
	tables   []CreateTable
	indexes  []CreateIndex
	triggers []CreateTrigger
}

var emptyFile = SqlFile{
	inserts:  []InsertInto{},
	views:    []CreateView{},
	tables:   []CreateTable{},
	indexes:  []CreateIndex{},
	triggers: []CreateTrigger{},
}

type Tuple[T1, T2 any] struct {
	first  T1
	second T2
}

func NewTuple[T1, T2 any](first T1, second T2) Tuple[T1, T2] {
	return Tuple[T1, T2]{
		first:  first,
		second: second,
	}
}

func (t Tuple[T1, T2]) First() T1 {
	return t.first
}

func (t Tuple[T1, T2]) Second() T2 {
	return t.second
}

type MigrationError struct {
	parsingFailed       Option[string]
	missingDependencies Option[Tuple[[]string, []string]]
	readFileFailed      Option[string]
	readSchemaFailed    Option[string]
	composed            Option[[]MigrationError]
	failedSteps         Option[[]string]
}
