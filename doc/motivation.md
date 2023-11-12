# Motivation

Database schema changes are a common requirement in our work.
However, the current approach to making these changes involves manually
writing SQL commands in separate files, resulting in a cumbersome and
lengthy sequence of files that document the history of schema changes.

Unfortunately, much of this history is irrelevant when it comes to
recreating the current database schema. It becomes challenging to identify
the necessary steps amidst the multitude of intermediate changes.
To access the current database schema, one must either rely on a
functioning system or sift through the extensive list of migrations,
searching for the buried statement that reveals the desired information.

In the past, a similar problem was tackled by introducing languages and tools
designed to facilitate the declaration of changes rather than relying on manual
specifications. Just as high-level languages were developed to reveal program
structures hidden within the complexities of assembly code, we now require a
similar approach for managing database schema changes. By embracing a higher
level of abstraction, we can focus on the essential aspects of our schema
modifications, rather than getting entangled in irrelevant details.

I believe that SQL or relational languages still provide an unique perspective
on databases, that hasn't been captured by ORMs. So I aimed to make SQL a first-class
citizen in the source code of my programs.

To facilitate the specification of database projects, Migrate provides a straightforward
approach. By placing a `db.toml` file in the project's root directory,
along with the relevant SQL files, users can define the desired schema.
Migrate then takes charge of calculating the necessary changes, executing them,
and maintaining a record of each modification. This streamlined process simplifies
database management and eliminates the need for manual intervention in many common cases.