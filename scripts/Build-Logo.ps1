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
# Read PNG pixel data (8-bit RGB or RGBA, non-interlaced) with .NET's zlib: no Python, no Pillow
# ============================================================================

function Read-PngImage([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt 8; $i++) { if ($bytes[$i] -ne $signature[$i]) { throw "$path is not a PNG" } }
    function Read-UInt32([byte[]]$b, [int]$at) { return ([uint32]$b[$at] -shl 24) -bor ([uint32]$b[$at + 1] -shl 16) -bor ([uint32]$b[$at + 2] -shl 8) -bor [uint32]$b[$at + 3] }

    $width = 0; $height = 0; $channels = 0
    $idat = [System.IO.MemoryStream]::new()
    $pos = 8
    while ($pos -lt $bytes.Length) {
        $length = [int](Read-UInt32 $bytes $pos)
        $type = [System.Text.Encoding]::ASCII.GetString($bytes, $pos + 4, 4)
        $data = $pos + 8
        switch ($type) {
            'IHDR' {
                $width = [int](Read-UInt32 $bytes $data); $height = [int](Read-UInt32 $bytes ($data + 4))
                $bitDepth = $bytes[$data + 8]; $colorType = $bytes[$data + 9]; $interlace = $bytes[$data + 12]
                if ($bitDepth -ne 8 -or $interlace -ne 0 -or ($colorType -ne 2 -and $colorType -ne 6)) {
                    throw "Unsupported PNG (bit depth $bitDepth, color type $colorType, interlace $interlace): only 8-bit RGB/RGBA, non-interlaced"
                }
                $channels = if ($colorType -eq 6) { 4 } else { 3 }
            }
            'IDAT' { $idat.Write($bytes, $data, $length) }
        }
        if ($type -eq 'IEND') { break }
        $pos = $data + $length + 4   # skip the CRC
    }

    $idat.Position = 0
    $zlib = [System.IO.Compression.ZLibStream]::new($idat, [System.IO.Compression.CompressionMode]::Decompress)
    $raw = [System.IO.MemoryStream]::new(); $zlib.CopyTo($raw); $zlib.Dispose()
    $raw = $raw.ToArray()

    # Undo the per-row filters (PNG spec: None, Sub, Up, Average, Paeth).
    $stride = $width * $channels
    $prev = [byte[]]::new($stride)
    $pixels = [object[]]::new($height)
    for ($y = 0; $y -lt $height; $y++) {
        $offset = $y * ($stride + 1)
        $filter = $raw[$offset]
        $cur = [byte[]]::new($stride)
        for ($x = 0; $x -lt $stride; $x++) {
            $v = [int]$raw[$offset + 1 + $x]
            $a = if ($x -ge $channels) { [int]$cur[$x - $channels] } else { 0 }
            $b = [int]$prev[$x]
            $c = if ($x -ge $channels) { [int]$prev[$x - $channels] } else { 0 }
            $pred = switch ($filter) {
                0 { 0 }
                1 { $a }
                2 { $b }
                3 { [Math]::Floor(($a + $b) / 2) }
                4 {
                    $p = $a + $b - $c; $pa = [Math]::Abs($p - $a); $pb = [Math]::Abs($p - $b); $pc = [Math]::Abs($p - $c)
                    if ($pa -le $pb -and $pa -le $pc) { $a } elseif ($pb -le $pc) { $b } else { $c }
                }
                default { throw "Unknown PNG filter type $filter on row $y" }
            }
            $cur[$x] = [byte](($v + $pred) % 256)
        }
        $rowPixels = [object[]]::new($width)
        for ($px = 0; $px -lt $width; $px++) {
            $k = $px * $channels
            $rowPixels[$px] = @([int]$cur[$k], [int]$cur[$k + 1], [int]$cur[$k + 2])
        }
        $pixels[$y] = $rowPixels
        $prev = $cur
    }
    return [pscustomobject]@{ width = $width; height = $height; pixels = $pixels }
}

$pixelData = Read-PngImage $pngPath

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
