# Reading SQL

```bash
python3 archview_sql.py <repository> --out arch.html [--title name] [--no-source]
```

Needs Python 3.10 or newer.

Reads what each script creates — tables, views, procedures, functions,
triggers — and what each of those names. A view that selects from a table
depends on that table; a procedure that calls a procedure depends on it; a
foreign key is a table depending on a table. Those are the edges.

The text is tokenised, not matched with expressions: a table name inside a
comment or a string literal is not a dependency. `[dbo].[Thing]`,
`"Thing"` and `` `thing` `` are one name, and the schema is dropped, because
`dbo.Orders` and `Orders` are one table and counting them as two would split
a table from its own uses.

It does not understand SQL. It recognises the statements that declare
something and the places where a name can only be an object's name. A
dialect it has not met declares its objects some other way, and then the run
says how many statements it could not place rather than reporting none.

A name declared twice — the same table created in two scripts, which a
repository with a client copy and a server copy will have — is not guessed
at. No edge is drawn rather than a wrong one.
