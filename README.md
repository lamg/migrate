# Migrate

Migrate is a tool for migrating databases in a declarative way. It can be used from the command line or as a library.

# Installation

- Install [dotnet 8.0][0]
- Run the following script to install the command line tool

```shell
git clone https://github.com/lamg/migrate
git clone https://github.com/lamg/Migrate.SqlParser
cd migrate
dotnet restore
cd Cli
dotnet publish -c release --self-contained --runtime linux-x64
DEST=$HOME/.local/bin/mig
if [ -f $DEST ]; then
    rm $DEST
fi 
ln -s bin/release/net8.0/linux-x64/publish/mig $DEST 
```

# Usage

See [usage](doc/usage.md)

# Why Migrate?

See [motivation](doc/motivation.md)

# Contributing

See [contributing_guideline](doc/contributing_guideline.md)

# License

[Apache 2.0][1]

[0]: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
[1]: https://www.apache.org/licenses/LICENSE-2.0 
