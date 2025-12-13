#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fixes IDE1006 naming violations for private/protected members by adding underscore prefix.

.DESCRIPTION
    This script renames private and protected fields, properties, and methods from PascalCase/camelCase
    to _camelCase to comply with the .editorconfig naming convention.

.PARAMETER Path
    The path to scan for violations. Can be a file or directory.

.PARAMETER WhatIf
    Shows what would be changed without making changes.

.EXAMPLE
    ./Fix-IDE1006-PrivateProtectedMembers.ps1 -Path tests/Whizbang.Core.Tests -WhatIf
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter()]
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Patterns to match various member types
$patterns = @{
    'Field' = '(?<indent>\s*)(?<access>private|protected)\s+(?<modifiers>(?:static\s+)?(?:readonly\s+)?)(?<type>[\w<>,\[\]?]+)\s+(?<name>[A-Z][a-zA-Z0-9]*)\s*[=;]'
    'Property' = '(?<indent>\s*)(?<access>private|protected)\s+(?<modifiers>(?:static\s+)?(?:virtual\s+)?)(?<type>[\w<>,\[\]?]+)\s+(?<name>[A-Z][a-zA-Z0-9]*)\s*(?:=>|{)'
    'Method' = '(?<indent>\s*)(?<access>private|protected)\s+(?<modifiers>(?:static\s+)?(?:virtual\s+)?(?:async\s+)?)(?<returnType>[\w<>,\[\]?]+)\s+(?<name>[A-Z][a-zA-Z0-9]*)\s*\('
}

function Convert-ToCamelCase {
    param([string]$Name)

    # Already has underscore
    if ($Name.StartsWith('_')) {
        return $Name
    }

    # Convert first char to lowercase and add underscore
    return "_" + $Name.Substring(0, 1).ToLower() + $Name.Substring(1)
}

function Fix-File {
    param([string]$FilePath)

    $content = Get-Content -Path $FilePath -Raw
    $originalContent = $content
    $renames = @{}

    # Find all members that need renaming
    foreach ($patternName in $patterns.Keys) {
        $pattern = $patterns[$patternName]
        $matches = [regex]::Matches($content, $pattern)

        foreach ($match in $matches) {
            $oldName = $match.Groups['name'].Value
            $newName = Convert-ToCamelCase -Name $oldName

            # Skip if already has underscore or name unchanged
            if ($oldName -eq $newName) {
                continue
            }

            # Track this rename (avoid duplicates from multiple patterns)
            if (-not $renames.ContainsKey($oldName)) {
                $renames[$oldName] = $newName
                Write-Host "  Found ($patternName): $oldName -> $newName"
            }
        }
    }

    if ($renames.Count -eq 0) {
        return $false
    }

    # Apply all renames
    foreach ($oldName in $renames.Keys) {
        $newName = $renames[$oldName]

        # Use word boundaries to avoid partial matches
        # This regex will match the whole word only
        $content = $content -replace "\b$oldName\b", $newName
    }

    if ($WhatIf) {
        Write-Host "[WHATIF] Would update $FilePath with $($renames.Count) renames" -ForegroundColor Yellow
    }
    else {
        Set-Content -Path $FilePath -Value $content -NoNewline
        Write-Host "[UPDATED] $FilePath with $($renames.Count) renames" -ForegroundColor Green
    }

    return $true
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
    Write-Host "`nProcessing: $($file.Name)" -ForegroundColor White
    if (Fix-File -FilePath $file.FullName) {
        $updatedCount++
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Total files updated: $updatedCount / $($files.Count)" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan
