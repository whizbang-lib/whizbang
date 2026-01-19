#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fixes IDE1006 naming violations for private static readonly fields by adding underscore prefix.

.DESCRIPTION
    This script renames private static readonly fields from PascalCase to _camelCase to comply with
    the .editorconfig naming convention. It handles symbol renaming across all references in the file.

.PARAMETER Path
    The path to scan for violations. Can be a file or directory.

.PARAMETER WhatIf
    Shows what would be changed without making changes.

.EXAMPLE
    ./Fix-IDE1006-PrivateStaticReadonly.ps1 -Path tests/Whizbang.Core.Tests -WhatIf
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter()]
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Pattern to match: private static readonly FieldName
$pattern = '(?<indent>\s*)private\s+static\s+readonly\s+(?<type>[\w<>,\[\]]+)\s+(?<name>[A-Z]\w+)'

function Convert-ToCamelCase {
    param([string]$PascalCase)
    return "_" + $PascalCase.Substring(0, 1).ToLower() + $PascalCase.Substring(1)
}

function Fix-File {
    param([string]$FilePath)

    $content = Get-Content -Path $FilePath -Raw
    $originalContent = $content
    $changes = @()

    # Find all private static readonly fields
    $matches = [regex]::Matches($content, $pattern)

    foreach ($match in $matches) {
        $oldName = $match.Groups['name'].Value
        $newName = Convert-ToCamelCase -PascalCase $oldName

        # Skip if already has underscore prefix
        if ($oldName.StartsWith('_')) {
            continue
        }

        Write-Host "  Found: $oldName -> $newName"
        $changes += @{ Old = $oldName; New = $newName }

        # Replace the field declaration
        $oldDecl = $match.Value
        $newDecl = $oldDecl.Replace(" $oldName", " $newName")
        $content = $content.Replace($oldDecl, $newDecl)

        # Replace all references (word boundary to avoid partial matches)
        $content = $content -replace "\b$oldName\b", $newName
    }

    if ($changes.Count -gt 0) {
        if ($WhatIf) {
            Write-Host "[WHATIF] Would update $FilePath with $($changes.Count) changes" -ForegroundColor Yellow
        }
        else {
            Set-Content -Path $FilePath -Value $content -NoNewline
            Write-Host "[UPDATED] $FilePath with $($changes.Count) changes" -ForegroundColor Green
        }
        return $true
    }

    return $false
}

# Get files to process
if (Test-Path -Path $Path -PathType Leaf) {
    $files = @(Get-Item -Path $Path)
}
else {
    $files = Get-ChildItem -Path $Path -Recurse -Filter "*.cs"
}

Write-Host "Processing $($files.Count) file(s)..." -ForegroundColor Cyan

$updatedCount = 0
foreach ($file in $files) {
    Write-Host "`nProcessing: $($file.FullName)" -ForegroundColor White
    if (Fix-File -FilePath $file.FullName) {
        $updatedCount++
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Total files updated: $updatedCount / $($files.Count)" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan
