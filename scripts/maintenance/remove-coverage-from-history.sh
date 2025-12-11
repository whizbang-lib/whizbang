#!/bin/bash
#
# Remove coverage files from git history
# WARNING: This rewrites git history and requires force push
#

set -e

echo "🔍 Checking for coverage files in git history..."

# Search for coverage files
COVERAGE_FILES=$(git log --all --full-history --name-only --pretty=format: | grep -i coverage | sort -u || true)

if [ -z "$COVERAGE_FILES" ]; then
    echo "✅ No coverage files found in git history!"
    echo "ℹ️  Your .gitignore is working correctly - coverage files were never committed."
    exit 0
fi

echo "⚠️  Found coverage files in git history:"
echo "$COVERAGE_FILES"
echo ""

read -p "Do you want to remove these from git history? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "❌ Aborted"
    exit 1
fi

echo ""
echo "🚨 WARNING: This will rewrite git history!"
echo "   - All commit SHAs will change"
echo "   - Collaborators will need to re-clone or rebase"
echo "   - You'll need to force push to remote"
echo ""

read -p "Are you ABSOLUTELY sure? (type 'yes' to continue) " -r
if [[ ! $REPLY == "yes" ]]; then
    echo "❌ Aborted"
    exit 1
fi

echo ""
echo "🗑️  Removing coverage files from git history using git-filter-repo..."

# Check if git-filter-repo is installed
if ! command -v git-filter-repo &> /dev/null; then
    echo "❌ git-filter-repo is not installed"
    echo ""
    echo "Install it with one of:"
    echo "  brew install git-filter-repo"
    echo "  pip install git-filter-repo"
    echo ""
    exit 1
fi

# Backup current branch
CURRENT_BRANCH=$(git branch --show-current)
echo "📌 Current branch: $CURRENT_BRANCH"

# Remove coverage files using git-filter-repo
git-filter-repo \
    --path-glob '**/coverage*' \
    --path-glob '**/TestResults/*coverage*' \
    --path-glob 'coverage*.xml' \
    --path-glob 'coverage*.json' \
    --invert-paths \
    --force

echo ""
echo "✅ Coverage files removed from git history!"
echo ""
echo "📋 Next steps:"
echo "   1. Verify the repository: git log --all --oneline"
echo "   2. Force push: git push --force --all"
echo "   3. Force push tags: git push --force --tags"
echo ""
echo "⚠️  Team members will need to:"
echo "   git fetch origin"
echo "   git reset --hard origin/$CURRENT_BRANCH"
