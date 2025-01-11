# Import [Goose][Goose] migrations

This feature allows to import [Goose][Goose] migrations by combining all
`up` steps found in the Goose directory. You can see the resulting script or executing it in the current directory, which should be empty to avoid conflicts.

Generate import script without execution:

```sh
mig import -d /path/to/goose/migrations
```

Import and execute into current directory:

```sh
mig import -d /path/to/goose/migrations -e
```

[Goose]: https://github.com/pressly/goose