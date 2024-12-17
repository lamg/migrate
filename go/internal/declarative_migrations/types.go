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
