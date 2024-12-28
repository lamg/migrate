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
			if !visit(node) {
				panic("cycle detected in graph")
			}
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
	if g.graph == nil {
		return false
	}

	_, ok := g.graph[x]
	return ok
}
