#!/bin/bash

dotnet tool restore
dotnet paket restore
cd Cli
dotnet publish -c Release
dotnet pack
dotnet tool uninstall -g migtool
dotnet tool install -g migtool --add-source nupkg
