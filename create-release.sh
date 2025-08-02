#!/bin/bash

# Nino Release Helper Script
# Creates a git tag to trigger the release workflow

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to print colored output
print_color() {
    printf "${1}${2}${NC}\n"
}

print_usage() {
    echo "Usage: $0 <version> [release-type]"
    echo ""
    echo "Arguments:"
    echo "  version       Semantic version (e.g., 1.2.3)"
    echo "  release-type  Optional: alpha, beta, rc (for pre-releases)"
    echo ""
    echo "Examples:"
    echo "  $0 1.2.3                    # Creates stable release v1.2.3"
    echo "  $0 1.2.3 beta               # Creates pre-release v1.2.3-beta.1"
    echo "  $0 1.2.3 alpha              # Creates pre-release v1.2.3-alpha.1"
    echo "  $0 1.2.3 rc                 # Creates pre-release v1.2.3-rc.1"
    echo ""
    echo "This script will:"
    echo "  1. Validate the version format"
    echo "  2. Check for existing tags"
    echo "  3. Create and push the git tag"
    echo "  4. The GitHub Actions will handle the rest!"
}

# Check arguments
if [[ $# -lt 1 || $# -gt 2 ]]; then
    print_color $RED "❌ Invalid number of arguments"
    print_usage
    exit 1
fi

VERSION=$1
RELEASE_TYPE=${2:-""}

# Validate semantic version format
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    print_color $RED "❌ Invalid version format: $VERSION"
    print_color $YELLOW "Expected format: X.Y.Z (e.g., 1.2.3)"
    exit 1
fi

# Validate release type
if [[ -n "$RELEASE_TYPE" ]]; then
    if [[ ! "$RELEASE_TYPE" =~ ^(alpha|beta|rc)$ ]]; then
        print_color $RED "❌ Invalid release type: $RELEASE_TYPE"
        print_color $YELLOW "Supported types: alpha, beta, rc"
        exit 1
    fi
fi

# Build tag name
if [[ -n "$RELEASE_TYPE" ]]; then
    # Find the next pre-release number
    EXISTING_TAGS=$(git tag -l "v$VERSION-$RELEASE_TYPE.*" | sort -V)
    if [[ -n "$EXISTING_TAGS" ]]; then
        LAST_TAG=$(echo "$EXISTING_TAGS" | tail -n1)
        LAST_NUM=$(echo "$LAST_TAG" | grep -o '[0-9]*$')
        NEXT_NUM=$((LAST_NUM + 1))
    else
        NEXT_NUM=1
    fi
    TAG_NAME="v$VERSION-$RELEASE_TYPE.$NEXT_NUM"
    IS_PRERELEASE=true
else
    TAG_NAME="v$VERSION"
    IS_PRERELEASE=false
fi

print_color $BLUE "🏷️  Preparing to create tag: $TAG_NAME"

# Check if tag already exists
if git tag -l | grep -q "^$TAG_NAME$"; then
    print_color $RED "❌ Tag $TAG_NAME already exists!"
    print_color $YELLOW "Existing tags for this version:"
    git tag -l "v$VERSION*" | sort -V
    exit 1
fi

# Check if we're on main branch
CURRENT_BRANCH=$(git branch --show-current)
if [[ "$CURRENT_BRANCH" != "main" ]]; then
    print_color $YELLOW "⚠️  You're on branch '$CURRENT_BRANCH', not 'main'"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        print_color $YELLOW "Cancelled"
        exit 0
    fi
fi

# Check for uncommitted changes
if [[ -n $(git status --porcelain) ]]; then
    print_color $YELLOW "⚠️  You have uncommitted changes:"
    git status --short
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        print_color $YELLOW "Cancelled"
        exit 0
    fi
fi

# Confirm release
echo
print_color $BLUE "📋 Release Summary:"
echo "  Version: $VERSION"
echo "  Tag: $TAG_NAME"
echo "  Pre-release: $IS_PRERELEASE"
echo "  Branch: $CURRENT_BRANCH"
echo

read -p "Create this release? (y/N): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    print_color $YELLOW "Cancelled"
    exit 0
fi

# Create and push tag
print_color $BLUE "🚀 Creating release..."

# Create annotated tag
git tag -a "$TAG_NAME" -m "Release $TAG_NAME"
print_color $GREEN "✅ Created tag: $TAG_NAME"

# Push tag
git push origin "$TAG_NAME"
print_color $GREEN "✅ Pushed tag to origin"

echo
print_color $GREEN "🎉 Release $TAG_NAME created successfully!"
echo
print_color $BLUE "Next steps:"
echo "  1. 🔍 Monitor the release workflow: https://github.com/$(git config --get remote.origin.url | sed 's/.*[:/]\([^/]*\/[^/]*\)\.git$/\1/')/actions"
echo "  2. 📝 The workflow will automatically:"
echo "     - Update version files"
echo "     - Build and test"
echo "     - Create GitHub release"
echo "     - Publish to NuGet"
echo "     - Run benchmarks"
echo
print_color $YELLOW "⏰ The release process typically takes 5-10 minutes to complete."