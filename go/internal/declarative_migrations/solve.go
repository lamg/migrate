package declarative_migrations

type Set[T comparable] map[T]struct{}

type Graph[T comparable] struct {
	graph map[T]Set[T]
}

func NewGraph[T comparable]() *Graph[T] {
	return &Graph[T]{graph: make(map[T]Set[T])}
}

func (g *Graph[T]) addEdge(u, v T) {
	if g.graph[u] == nil {
		g.graph[u] = make(Set[T])
	}

	g.graph[u][v] = struct{}{}
}

func (g *Graph[T]) topologicalSort() []T {
	visited := make(Set[T])
	temp := make(Set[T])
	order := make([]T, 0)

	var visit func(node T) bool
	visit = func(node T) bool {
		if _, exists := temp[node]; exists {
			return false // Cycle detected
		}
		if _, exists := visited[node]; exists {
			return true
		}

		temp[node] = struct{}{}

		for adj := range g.graph[node] {
			if !visit(adj) {
				return false
			}
		}

		delete(temp, node)
		visited[node] = struct{}{}
		order = append([]T{node}, order...)
		return true
	}

	for node := range g.graph {
		if _, exists := visited[node]; !exists {
			visit(node)
		}
	}
	return order
}

func (g *Graph[T]) hasEdge(x, y T) bool {
	if g.graph == nil {
		return false
	}
	if neighbors, exists := g.graph[x]; exists {
		_, hasEdge := neighbors[y]
		return hasEdge
	}
	return false
}

func (g *Graph[T]) isDependency(x T) bool {
	_, ok := g.graph[x]
	return ok
}

func dependentRelations(file SqlFile) (*Graph[string], []string) {
	g := NewGraph[string]()
	return g, []string{}
}

func tableDifferences(left, right SqlFile) (adds, removes, renames []string) {
	return
}

type fileSorted struct {
	file            SqlFile
	sortedRelations []string
}

func tableMigrations(left, right fileSorted) (creates []CreateTable, drops []string, renames []string) {
	return
}

func sortFile(file SqlFile) fileSorted {
	return fileSorted{file: file, sortedRelations: []string{}}
}

func tableMigrationsSql(left, right fileSorted) []string {
	return []string{}
}

type setSortSql struct {
	set    Set[string]
	sql    func(string) string
	sorted []string
}

func simpleMigrationSql(left, right setSortSql) []string {
	return []string{}
}

func viewMigrationSql(left, right fileSorted) []string {
	return []string{}
}

func indexMigrationSql(left, right fileSorted) []string {
	return []string{}
}

func triggerMigrationSql(left, right fileSorted) []string {
	return []string{}
}

func columnMigrations(left, right []CreateTable) []string {
	return []string{}
}
