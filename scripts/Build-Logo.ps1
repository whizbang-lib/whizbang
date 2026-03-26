#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Reads the canonical logo source files (txt + png) and generates embedded data
    for all consumers (C#, PowerShell, VSCode extension).

.DESCRIPTION
    Source files:
      logo/whizbang-banner.txt          — character grid (human-editable)
      logo/whizbang-banner-colors.png   — 84x9 pixel color map (human-editable)

    Generated outputs:
      src/Whizbang.Core/Diagnostics/WhizbangBanner.Generated.cs
      scripts/lib/WhizbangBanner.Data.ps1
      ../whizbang-vscode/src/assets/whizbang-banner.txt

    Also renders the banner as a visual test.

.PARAMETER SkipVscode
    Skip copying to the VSCode extension (useful if that repo isn't present).

.PARAMETER TestOnly
    Only render the visual test, don't generate output files.
#>
[CmdletBinding()]
param(
    [switch]$SkipVscode,
    [switch]$TestOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logoDir = Join-Path $repoRoot "logo"
$txtPath = Join-Path $logoDir "whizbang-banner.txt"
$pngPath = Join-Path $logoDir "whizbang-banner-colors.png"

# ============================================================================
# Validate source files
# ============================================================================

if (-not (Test-Path $txtPath)) {
    Write-Error "Banner text file not found: $txtPath"
}
if (-not (Test-Path $pngPath)) {
    Write-Error "Banner color PNG not found: $pngPath"
}

# ============================================================================
# Read banner text
# ============================================================================

$bannerLines = Get-Content $txtPath -Encoding UTF8
$bannerWidth = 84
$bannerHeight = $bannerLines.Count

Write-Host "Read $bannerHeight lines from banner text (width=$bannerWidth)" -ForegroundColor Cyan

foreach ($i in 0..($bannerHeight - 1)) {
    $lineLen = $bannerLines[$i].Length
    if ($lineLen -ne $bannerWidth) {
        Write-Warning "Line $i has length $lineLen (expected $bannerWidth)"
    }
}

# ============================================================================
# Read PNG pixel data using Python + Pillow
# ============================================================================

$pythonScript = @"
import sys, json
from PIL import Image
img = Image.open(sys.argv[1])
w, h = img.size
pixels = []
for y in range(h):
    row = []
    for x in range(w):
        r, g, b = img.getpixel((x, y))[:3]
        row.append([r, g, b])
    pixels.append(row)
json.dump({"width": w, "height": h, "pixels": pixels}, sys.stdout)
"@

$tempPy = [System.IO.Path]::GetTempFileName() + ".py"
Set-Content -Path $tempPy -Value $pythonScript -Encoding UTF8

try {
    $pixelJson = python3 $tempPy $pngPath
    $pixelData = $pixelJson | ConvertFrom-Json
} finally {
    Remove-Item $tempPy -ErrorAction SilentlyContinue
}

if ($pixelData.width -ne $bannerWidth -or $pixelData.height -ne $bannerHeight) {
    Write-Error "PNG dimensions ($($pixelData.width)x$($pixelData.height)) don't match text ($bannerWidth x $bannerHeight)"
}

Write-Host "Read $($pixelData.width)x$($pixelData.height) pixels from PNG" -ForegroundColor Cyan

# ============================================================================
# Build flat color byte array (row-major, 3 bytes per pixel: R,G,B)
# ============================================================================

$colorBytes = [System.Collections.Generic.List[byte]]::new($bannerWidth * $bannerHeight * 3)
for ($row = 0; $row -lt $bannerHeight; $row++) {
    for ($col = 0; $col -lt $bannerWidth; $col++) {
        $pixel = $pixelData.pixels[$row][$col]
        $colorBytes.Add([byte]$pixel[0])
        $colorBytes.Add([byte]$pixel[1])
        $colorBytes.Add([byte]$pixel[2])
    }
}

$colorByteArray = $colorBytes.ToArray()
$colorBase64 = [Convert]::ToBase64String($colorByteArray)

Write-Host "Color data: $($colorByteArray.Length) bytes ($($colorBase64.Length) chars base64)" -ForegroundColor Cyan

# ============================================================================
# Visual Test - Render the banner with ANSI true color
# ============================================================================

$esc = [char]27
$bgR = 45; $bgG = 55; $bgB = 72
$bg = "${esc}[48;2;${bgR};${bgG};${bgB}m"
$reset = "${esc}[0m"
$starChars = @('.', [char]0x00B7, [char]0x2219, '*', [char]0x22C5, [char]0x2726)

Write-Host ""
Write-Host "=== Visual Test ===" -ForegroundColor Yellow
Write-Host ""

for ($row = 0; $row -lt $bannerHeight; $row++) {
    $line = $bannerLines[$row]
    # Coalesce adjacent same-color chars into segments for efficiency
    $col = 0
    while ($col -lt $bannerWidth) {
        $idx = ($row * $bannerWidth + $col) * 3
        $r = $colorByteArray[$idx]
        $g = $colorByteArray[$idx + 1]
        $b = $colorByteArray[$idx + 2]

        # Find run of same color
        $runStart = $col
        while ($col -lt $bannerWidth) {
            $nextIdx = ($row * $bannerWidth + $col) * 3
            if ($colorByteArray[$nextIdx] -ne $r -or
                $colorByteArray[$nextIdx + 1] -ne $g -or
                $colorByteArray[$nextIdx + 2] -ne $b) {
                break
            }
            $col++
        }

        $text = $line.Substring($runStart, $col - $runStart)
        $isBg = ($r -eq $bgR -and $g -eq $bgG -and $b -eq $bgB)

        if ($isBg) {
            # Background segment - sprinkle stars
            foreach ($ch in $text.ToCharArray()) {
                if ($ch -eq ' ' -and (Get-Random -Minimum 0 -Maximum 12) -eq 0) {
                    $brightness = Get-Random -Minimum 220 -Maximum 255
                    $starCh = $starChars[(Get-Random -Minimum 0 -Maximum $starChars.Count)]
                    Write-Host "${bg}${esc}[38;2;${brightness};$($brightness + 5);$($brightness + 10)m${starCh}${reset}" -NoNewline
                } else {
                    Write-Host "${bg}${esc}[38;2;${r};${g};${b}m${ch}${reset}" -NoNewline
                }
            }
        } else {
            Write-Host "${bg}${esc}[38;2;${r};${g};${b}m${text}${reset}" -NoNewline
        }
    }
    # EOL padding
    Write-Host "${bg}  ${reset}"
}

Write-Host ""

if ($TestOnly) {
    Write-Host "Test-only mode, skipping generation." -ForegroundColor Yellow
    return
}

# ============================================================================
# Generate C# file
# ============================================================================

$csPath = Join-Path $repoRoot "src" "Whizbang.Core" "Diagnostics" "WhizbangBanner.Generated.cs"

$plainBannerCs = ""
foreach ($line in $bannerLines) {
    $escaped = $line -replace '\\', '\\' -replace '"', '\"'
    $plainBannerCs += "    `"$escaped`",`n"
}
$plainBannerCs = $plainBannerCs.TrimEnd("`n", ",")

# Format color bytes as comma-separated values, 84*3 bytes per line for readability
$colorBytesCs = ""
for ($row = 0; $row -lt $bannerHeight; $row++) {
    $rowBytes = @()
    for ($col = 0; $col -lt $bannerWidth; $col++) {
        $idx = ($row * $bannerWidth + $col) * 3
        $rowBytes += "$($colorByteArray[$idx]),$($colorByteArray[$idx+1]),$($colorByteArray[$idx+2])"
    }
    $colorBytesCs += "    // Row $row`n"
    # Split into chunks of ~12 pixels per line for readability
    $chunks = @()
    for ($i = 0; $i -lt $rowBytes.Count; $i += 12) {
        $end = [Math]::Min($i + 12, $rowBytes.Count)
        $chunks += ($rowBytes[$i..($end-1)] -join ", ")
    }
    $colorBytesCs += "    " + ($chunks -join ",`n    ")
    if ($row -lt $bannerHeight - 1) {
        $colorBytesCs += ","
    }
    $colorBytesCs += "`n"
}

$csContent = @"
// <auto-generated by Build-Logo.ps1 - do not hand-edit>
// Source: logo/whizbang-banner.txt + logo/whizbang-banner-colors.png

namespace Whizbang.Core.Diagnostics;

public static partial class WhizbangBanner {
  private const int BANNER_ROWS = $bannerHeight;

  private static readonly string[] _plainBanner =
  [
$plainBannerCs
  ];

  // ${bannerWidth}x${bannerHeight}x3 bytes: RGB per character, row-major.
  // Pixels matching background ($bgR,$bgG,$bgB) are star-eligible background segments.
  private static ReadOnlySpan<byte> _colorData =>
  [
$colorBytesCs  ];
}
"@

Set-Content -Path $csPath -Value $csContent -Encoding UTF8 -NoNewline
Write-Host "Generated: $csPath" -ForegroundColor Green

# ============================================================================
# Generate PowerShell data module
# ============================================================================

$psDataPath = Join-Path $repoRoot "scripts" "lib" "WhizbangBanner.Data.ps1"

$plainBannerPs = ""
foreach ($line in $bannerLines) {
    $escaped = $line -replace "'", "''"
    $plainBannerPs += "    '$escaped'`n"
}
$plainBannerPs = $plainBannerPs.TrimEnd("`n")

$psContent = @"
# <auto-generated by Build-Logo.ps1 - do not hand-edit>
# Source: logo/whizbang-banner.txt + logo/whizbang-banner-colors.png

Set-StrictMode -Version Latest

`$script:BannerWidth = $bannerWidth
`$script:BannerHeight = $bannerHeight

`$script:PlainBanner = @(
$plainBannerPs
)

# Base64-encoded RGB color data (${bannerWidth}x${bannerHeight}x3 = $($colorByteArray.Length) bytes)
# Pixels matching background (${bgR},${bgG},${bgB}) are star-eligible background segments.
`$script:ColorDataBase64 = '$colorBase64'
"@

Set-Content -Path $psDataPath -Value $psContent -Encoding UTF8 -NoNewline
Write-Host "Generated: $psDataPath" -ForegroundColor Green

# ============================================================================
# Generate TypeScript data file for VSCode extension
# ============================================================================

if (-not $SkipVscode) {
    $vscodeAssetsPath = Join-Path $repoRoot ".." "whizbang-vscode" "src" "assets"
    if (Test-Path (Split-Path $vscodeAssetsPath -Parent)) {
        if (-not (Test-Path $vscodeAssetsPath)) {
            New-Item -ItemType Directory -Path $vscodeAssetsPath -Force | Out-Null
        }

        # Generate TypeScript banner data file
        $tsDataPath = Join-Path (Split-Path $vscodeAssetsPath -Parent) "bannerData.generated.ts"

        $plainBannerTs = ""
        foreach ($line in $bannerLines) {
            $escaped = $line -replace '\\', '\\' -replace "'", "\'"
            $plainBannerTs += "  '$escaped',`n"
        }
        $plainBannerTs = $plainBannerTs.TrimEnd("`n", ",")

        $tsContent = @"
// <auto-generated by Build-Logo.ps1 - do not hand-edit>
// Source: logo/whizbang-banner.txt + logo/whizbang-banner-colors.png

export const BANNER_WIDTH = $bannerWidth;
export const BANNER_HEIGHT = $bannerHeight;
export const BACKGROUND_R = $bgR;
export const BACKGROUND_G = $bgG;
export const BACKGROUND_B = $bgB;

export const plainBanner: string[] = [
$plainBannerTs
];

// Base64-encoded RGB color data (${bannerWidth}x${bannerHeight}x3 = $($colorByteArray.Length) bytes)
export const colorDataBase64 = '$colorBase64';
"@

        Set-Content -Path $tsDataPath -Value $tsContent -Encoding UTF8 -NoNewline
        Write-Host "Generated: $tsDataPath" -ForegroundColor Green
    } else {
        Write-Warning "VSCode extension repo not found, skipping TS generation"
    }
}

Write-Host ""
Write-Host "Build-Logo complete!" -ForegroundColor Green
