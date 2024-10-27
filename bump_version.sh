#!/bin/bash

# This script bumps the version number in src/Version.cs, then bumps the version number in [Nino, Nino.Core, Nino.Generator]/*.csproj files.

# Make sure it has only one argument
if [ $# -ne 1 ]; then
    echo "Usage: bump_version.sh <version>"
    exit 1
fi

# Make sure the argument is a valid version number
if ! [[ $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Invalid version number: $1"
    exit 1
fi

NEW_VERSION=$1

# Bump the version number in src/Version.cs
VERSION_FILE="src/Version.cs"

# [assembly: AssemblyVersion("ver")]
OLD_VERSION=$(sed -n 's/.*AssemblyVersion("\([^"]*\)").*/\1/p' $VERSION_FILE)

if [ -z "$OLD_VERSION" ]; then
    echo "Failed to find AssemblyVersion in $VERSION_FILE"
    exit 1
fi

echo "Bumping AssemblyVersion number in $VERSION_FILE from $OLD_VERSION to $NEW_VERSION"

sed -i "" "s/AssemblyVersion(\"$OLD_VERSION\")/AssemblyVersion(\"$NEW_VERSION\")/" $VERSION_FILE

# [assembly: AssemblyFileVersion("ver")]
OLD_VERSION=$(sed -n 's/.*AssemblyFileVersion("\([^"]*\)").*/\1/p' $VERSION_FILE)

if [ -z "$OLD_VERSION" ]; then
    echo "Failed to find AssemblyFileVersion in $VERSION_FILE"
    exit 1
fi

echo "Bumping AssemblyFileVersion number in $VERSION_FILE from $OLD_VERSION to $NEW_VERSION"

sed -i "" "s/AssemblyFileVersion(\"$OLD_VERSION\")/AssemblyFileVersion(\"$NEW_VERSION\")/" $VERSION_FILE

# Bump the version number in [Nino, Nino.Core, Nino.Generator]/*.csproj files
PROJS=$(find src/Nino src/Nino.Core src/Nino.Generator -name '*.csproj')

for PROJ in $PROJS; do
    # <Version>ver</Version>
    OLD_VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' $PROJ)

    if [ -z "$OLD_VERSION" ]; then
        echo "Failed to find Version in $PROJ"
        exit 1
    fi

    echo "Bumping Version number in $PROJ from $OLD_VERSION to $NEW_VERSION"

    sed -i "" "s/<Version>$OLD_VERSION<\/Version>/<Version>$NEW_VERSION<\/Version>/" $PROJ
done