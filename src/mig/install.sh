#!/bin/bash

dotnet restore
dotnet publish -c Release
dotnet pack
dotnet tool uninstall -g migtool
dotnet tool install -g migtool --add-source nupkg
