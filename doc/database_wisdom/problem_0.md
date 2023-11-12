# `NULL`, an open door to semantic inconsistencies

## The `NULL` manager

Let's look at the following table, which stores data about employees in a company:

`employee`

| id  | name | manager_id |
| :-: | :--: | :--------: |
|  0  | Luis |     0      |
|  1  | Mary |     0      |
|  2  | Kurt |    NULL    |

It represents the fact that Kurt has a manager, but it is unknown.
Let's see what happens with the following query:

```sql
SELECT * FROM employee WHERE manager_id <> 0
```

This query returns no results. However, in real life, Kurt has a manager,
whose `id` may be different from 0. `NULL` is the wrong representation for this
fact, and since is just a value in a table among many others, this semantic
inconsistency can be easily overlooked.

From the point of view of established relational theory `NULL`, values
should be avoided. One way to solving our problem would be as follows:

`employee_known_manager`

| id  | name | manager_id |
| :-: | :--: | :--------: |
|  0  | Luis |     0      |
|  1  | Mary |     0      |

`employee_unknown_manager`

| id  | name |
| :-: | :--: |
|  0  | Kurt |

With the absence of `NULL` and tables containing only data that they can reliably
represent, then the situation we are in becomes clear. Some buried `NULL` values
will not break the logic of our queries.

This is also consistent with what happens in statically typed programming languages:
Once the type of a variable is determined, certain operations can be
safely executed.

Now the employees are in two different tables, and someone might argue that this
creates a more complex database structure, but that's a bad argument because:

- is indeed more complex, but fails to recognize that the simpler structure is
  wrong, while the complex is right.
- we have means to deal with this particular complexity, they are called
  _views_

_Aside_:
Keep in mind that people who insist with bad arguments may be
hiding something about themselves or the social pressures they are under.

The view would be this:

```sql
CREATE VIEW employee AS
SELECT id, name FROM employee_known_manager
UNION
SELECT id, name FROM employee_unknown_manager
```

In general, the `NULL` value has been recognized as a [mistake][1] by its own creator.

Further reading:

- [SQL and Relational Theory][0] by C.J. Date

[0]: https://www.oreilly.com/library/view/sql-and-relational/9781449319724/ch04s04.html
[1]: https://www.infoq.com/presentations/Null-References-The-Billion-Dollar-Mistake-Tony-Hoare/
