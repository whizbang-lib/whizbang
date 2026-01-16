<#
.SYNOPSIS
Remove coverage files from git history

.DESCRIPTION
Removes coverage files from git history using git-filter-repo.
WARNING: This rewrites git history and requires force push.

.EXAMPLE
pwsh scripts/maintenance/Remove-CoverageFromHistory.ps1

.NOTES
Requires PowerShell 7+ and git-filter-repo to be installed.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Write-Host "🔍 Checking for coverage files in git history..." -ForegroundColor Cyan
Write-Host ""

# Search for coverage files
$coverageFiles = git log --all --full-history --name-only --pretty=format: |
    Where-Object { $_ -match "coverage" } |
    Sort-Object -Unique

if (-not $coverageFiles) {
    Write-Host "✅ No coverage files found in git history!" -ForegroundColor Green
    Write-Host "ℹ️  Your .gitignore is working correctly - coverage files were never committed." -ForegroundColor Blue
    exit 0
}

Write-Host "⚠️  Found coverage files in git history:" -ForegroundColor Yellow
$coverageFiles | ForEach-Object { Write-Host "   $_" }
Write-Host ""

$confirm1 = Read-Host "Do you want to remove these from git history? (y/N)"
if ($confirm1 -notmatch "^[Yy]$") {
    Write-Host "❌ Aborted" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "🚨 WARNING: This will rewrite git history!" -ForegroundColor Red
Write-Host "   - All commit SHAs will change"
Write-Host "   - Collaborators will need to re-clone or rebase"
Write-Host "   - You'll need to force push to remote"
Write-Host ""

$confirm2 = Read-Host "Are you ABSOLUTELY sure? (type 'yes' to continue)"
if ($confirm2 -ne "yes") {
    Write-Host "❌ Aborted" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "🗑️  Removing coverage files from git history using git-filter-repo..." -ForegroundColor Cyan
Write-Host ""

# Check if git-filter-repo is installed
try {
    $null = Get-Command git-filter-repo -ErrorAction Stop
} catch {
    Write-Host "❌ git-filter-repo is not installed" -ForegroundColor Red
    Write-Host ""
    Write-Host "Install it with one of:"
    Write-Host "  brew install git-filter-repo"
    Write-Host "  pip install git-filter-repo"
    Write-Host ""
    exit 1
}

# Get current branch
$currentBranch = git branch --show-current
Write-Host "📌 Current branch: $currentBranch" -ForegroundColor Blue
Write-Host ""

# Remove coverage files using git-filter-repo
git-filter-repo `
    --path-glob '**/coverage*' `
    --path-glob '**/TestResults/*coverage*' `
    --path-glob 'coverage*.xml' `
    --path-glob 'coverage*.json' `
    --invert-paths `
    --force

Write-Host ""
Write-Host "✅ Coverage files removed from git history!" -ForegroundColor Green
Write-Host ""
Write-Host "📋 Next steps:" -ForegroundColor Cyan
Write-Host "   1. Verify the repository: git log --all --oneline"
Write-Host "   2. Force push: git push --force --all"
Write-Host "   3. Force push tags: git push --force --tags"
Write-Host ""
Write-Host "⚠️  Team members will need to:" -ForegroundColor Yellow
Write-Host "   git fetch origin"
Write-Host "   git reset --hard origin/$currentBranch"
Write-Host ""
