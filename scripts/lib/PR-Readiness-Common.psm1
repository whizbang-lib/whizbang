#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Shared module for Whizbang PR readiness scripts.

.DESCRIPTION
    Provides common functions used by Run-Tests.ps1, Run-Sonar.ps1, and Run-PR.ps1:
    - Branded header with ASCII art Whizbang logo
    - Tee logging with independent console/file verbosity
    - JSON output for machine-readable results
    - JSONL history logging and run estimation
    - AI instructions on failure

    Import this module in scripts:
        Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force
#>

Set-StrictMode -Version Latest

# ============================================================================
# ASCII Art Logo & Branded Header
# ============================================================================

# The Whizbang ASCII art wordmark with per-letter gradient coloring.
# Brand details:
#   - "W" is a flowing sine-wave shape (matching the W! logo mark)
#   - "ba" forms an infinity symbol (∞)
#   - "n" has a dot on its foot
#   - Each letter is colored to match the logo gradient:
#     W=Cyan, h=Blue, i=Magenta, z=DarkMagenta, b=Red, a=DarkYellow, n=Yellow, g=Yellow

# Each entry: array of strings (one per line), and the ForegroundColor for that letter
$script:LogoLetters = @(
    @{
        # W - flowing sine-wave shape
        Lines = @(
            " __        __ "
            " \ \      / / "
            "  \ \    / /  "
            "   \ \/\/ /   "
            "    \    /    "
            "     \/\/     "
        )
        Color = "Cyan"
    }
    @{
        # h
        Lines = @(
            " _      "
            "| |     "
            "| |__   "
            "| '_ \  "
            "| | | | "
            "|_| |_| "
        )
        Color = "Blue"
    }
    @{
        # i
        Lines = @(
            " _  "
            "(_) "
            " _  "
            "| | "
            "| | "
            "|_| "
        )
        Color = "Magenta"
    }
    @{
        # z
        Lines = @(
            "      "
            " ____ "
            "|_  / "
            " / /  "
            "/ /__ "
            "/____| "
        )
        Color = "DarkMagenta"
    }
    @{
        # b - left half of infinity
        Lines = @(
            " _      "
            "| |     "
            "| |__   "
            "| '_ \  "
            "| |_) | "
            "|_.__/  "
        )
        Color = "Red"
    }
    @{
        # a - right half of infinity
        Lines = @(
            "       "
            "  __ _ "
            " / _` |"
            "| (_| |"
            " \__,_|"
            "       "
        )
        Color = "DarkYellow"
    }
    @{
        # n - with dot on foot
        Lines = @(
            "        "
            " _ __   "
            "| '_ \  "
            "| | | | "
            "|_| |_| "
            "    .   "
        )
        Color = "Yellow"
    }
    @{
        # g
        Lines = @(
            "        "
            "  __ _  "
            " / _` | "
            "| (_| | "
            " \__, | "
            " |___/  "
        )
        Color = "Yellow"
    }
)

function Write-WhizbangHeader {
    <#
    .SYNOPSIS
        Prints the branded Whizbang header with ASCII art logo and config box.

    .PARAMETER ScriptName
        Name of the script (e.g., "Test Runner", "PR Runner", "Sonar Runner")

    .PARAMETER Version
        Script version string (e.g., "1.0")

    .PARAMETER Params
        Hashtable of key=value pairs to display in the config box (e.g., @{ Mode = "Ai"; Coverage = "On" })

    .PARAMETER Estimate
        Optional estimation string (e.g., "~50s (avg 48.3s, p85 55.1s)")
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptName,

        [string]$Version = "1.0",

        [hashtable]$Params = @{},

        [string]$Estimate = ""
    )

    Write-Host ""

    # Print "W!" in large text
    Write-Host "                W" -ForegroundColor Cyan -NoNewline
    Write-Host "!" -ForegroundColor DarkMagenta
    Write-Host ""

    # Print ASCII art wordmark with per-letter gradient coloring
    $lineCount = $script:LogoLetters[0].Lines.Count
    for ($row = 0; $row -lt $lineCount; $row++) {
        Write-Host "  " -NoNewline
        foreach ($letter in $script:LogoLetters) {
            $line = if ($row -lt $letter.Lines.Count) { $letter.Lines[$row] } else { " " * $letter.Lines[0].Length }
            Write-Host $line -ForegroundColor $letter.Color -NoNewline
        }
        Write-Host ""
    }

    Write-Host ""

    # Build config line
    $configParts = @()
    foreach ($key in $Params.Keys | Sort-Object) {
        $configParts += "$key`: $($Params[$key])"
    }
    $configLine = $configParts -join " | "

    # Calculate box width (minimum 56, expand for long content)
    $titleLine = "  $ScriptName v$Version"
    $maxContentWidth = @($titleLine.Length, $configLine.Length + 4, 56) | Measure-Object -Maximum | Select-Object -ExpandProperty Maximum
    $boxWidth = [Math]::Min($maxContentWidth + 2, 80)
    $innerWidth = $boxWidth - 2

    # Print config box
    Write-Host "  ╔$("═" * $innerWidth)╗" -ForegroundColor Cyan
    Write-Host "  ║" -ForegroundColor Cyan -NoNewline
    Write-Host "$($titleLine.PadRight($innerWidth))" -ForegroundColor White -NoNewline
    Write-Host "║" -ForegroundColor Cyan

    if ($configLine) {
        Write-Host "  ║" -ForegroundColor Cyan -NoNewline
        Write-Host "$("  $configLine".PadRight($innerWidth))" -ForegroundColor White -NoNewline
        Write-Host "║" -ForegroundColor Cyan
    }

    if ($Estimate) {
        Write-Host "  ╠$("═" * $innerWidth)╣" -ForegroundColor Cyan
        Write-Host "  ║" -ForegroundColor Cyan -NoNewline
        Write-Host "$("  Estimated: $Estimate".PadRight($innerWidth))" -ForegroundColor White -NoNewline
        Write-Host "║" -ForegroundColor Cyan
    }

    Write-Host "  ╚$("═" * $innerWidth)╝" -ForegroundColor Cyan
    Write-Host ""
}

# ============================================================================
# Tee Logging
# ============================================================================

$script:TeeContext = $null

function Initialize-TeeLogging {
    <#
    .SYNOPSIS
        Sets up dual output with independent verbosity for console and log file.

    .PARAMETER LogFile
        Path to the log file.

    .PARAMETER ConsoleMode
        Console output verbosity level.

    .PARAMETER LogMode
        Log file output verbosity level. Defaults to ConsoleMode if not specified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LogFile,

        [string]$ConsoleMode = "All",

        [string]$LogMode = ""
    )

    if (-not $LogMode) { $LogMode = $ConsoleMode }

    # Ensure parent directory exists
    $logDir = Split-Path -Parent $LogFile
    if ($logDir -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    $script:TeeContext = @{
        LogFile     = $LogFile
        ConsoleMode = $ConsoleMode
        LogMode     = $LogMode
        StartTime   = [DateTime]::UtcNow
    }

    # Write header to log file
    $header = @(
        "════════════════════════════════════════════════════════════"
        "  Whizbang Log — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        "  Console Mode: $ConsoleMode | Log Mode: $LogMode"
        "════════════════════════════════════════════════════════════"
        ""
    )
    $header | Out-File -FilePath $LogFile -Encoding utf8
}

function Write-TeeHost {
    <#
    .SYNOPSIS
        Writes to console and/or log file respecting per-channel verbosity.

    .PARAMETER Message
        The message to write.

    .PARAMETER ForegroundColor
        Console foreground color.

    .PARAMETER Level
        Verbosity level: "Verbose" (only in non-Ai modes) or "Summary" (always shown).
        Default: "Summary"
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [string]$ForegroundColor = "",

        [ValidateSet("Verbose", "Summary")]
        [string]$Level = "Summary"
    )

    $isAiConsole = $script:TeeContext -and $script:TeeContext.ConsoleMode -match "^Ai"
    $isAiLog = $script:TeeContext -and $script:TeeContext.LogMode -match "^Ai"

    # Write to console if level permits
    $showOnConsole = ($Level -eq "Summary") -or (-not $isAiConsole)
    if ($showOnConsole) {
        if ($ForegroundColor) {
            Write-Host $Message -ForegroundColor $ForegroundColor
        }
        else {
            Write-Host $Message
        }
    }

    # Write to log file if tee is active and level permits
    if ($script:TeeContext -and $script:TeeContext.LogFile) {
        $showInLog = ($Level -eq "Summary") -or (-not $isAiLog)
        if ($showInLog) {
            $Message | Out-File -FilePath $script:TeeContext.LogFile -Append -Encoding utf8
        }
    }
}

function Stop-TeeLogging {
    <#
    .SYNOPSIS
        Finalizes the log file with footer and timing information.
    #>
    [CmdletBinding()]
    param()

    if (-not $script:TeeContext -or -not $script:TeeContext.LogFile) { return }

    $elapsed = ([DateTime]::UtcNow - $script:TeeContext.StartTime)
    $footer = @(
        ""
        "════════════════════════════════════════════════════════════"
        "  Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        "  Duration: $($elapsed.ToString('hh\:mm\:ss'))"
        "════════════════════════════════════════════════════════════"
    )
    $footer | Out-File -FilePath $script:TeeContext.LogFile -Append -Encoding utf8

    Write-Host "  Log written to: $($script:TeeContext.LogFile)" -ForegroundColor DarkGray

    $script:TeeContext = $null
}

# ============================================================================
# JSON Output
# ============================================================================

function ConvertTo-JsonResult {
    <#
    .SYNOPSIS
        Outputs a structured result as a single JSON object to stdout.

    .PARAMETER Result
        Hashtable or PSObject containing the result data.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Result
    )

    $Result | ConvertTo-Json -Depth 10 -Compress
}

# ============================================================================
# History & Estimation
# ============================================================================

function Write-HistoryEntry {
    <#
    .SYNOPSIS
        Appends a JSONL line to the specified history file.

    .PARAMETER FilePath
        Path to the JSONL history file (e.g., logs/test-runs.jsonl).

    .PARAMETER Entry
        Hashtable containing the entry data. A timestamp field is added automatically.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [hashtable]$Entry
    )

    # Ensure logs directory exists
    $logDir = Split-Path -Parent $FilePath
    if ($logDir -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    # Add timestamp if not present
    if (-not $Entry.ContainsKey("timestamp")) {
        $Entry["timestamp"] = (Get-Date).ToUniversalTime().ToString("o")
    }

    $json = $Entry | ConvertTo-Json -Depth 5 -Compress
    $json | Out-File -FilePath $FilePath -Append -Encoding utf8
}

function Get-RunEstimate {
    <#
    .SYNOPSIS
        Reads JSONL history and computes estimated duration statistics.

    .PARAMETER FilePath
        Path to the JSONL history file.

    .PARAMETER FilterKey
        Optional key to filter entries by (e.g., "mode").

    .PARAMETER FilterValue
        Value to match for the filter key (e.g., "AiUnit").

    .OUTPUTS
        Hashtable with avg, p85, min, max, stddev, formatted string — or $null if <2 entries.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string]$FilterKey = "",
        [string]$FilterValue = ""
    )

    if (-not (Test-Path $FilePath)) { return $null }

    $entries = Get-Content $FilePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Trim() } |
        ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } |
        Where-Object { $_ -ne $null }

    # Apply filter if specified
    if ($FilterKey -and $FilterValue) {
        $entries = $entries | Where-Object { $_.$FilterKey -eq $FilterValue }
    }

    $durations = @($entries | Where-Object { $_.duration_s } | ForEach-Object { [double]$_.duration_s })

    if ($durations.Count -lt 2) { return $null }

    $sorted = $durations | Sort-Object
    $avg = ($durations | Measure-Object -Average).Average
    $min = $sorted[0]
    $max = $sorted[-1]

    # P85
    $p85Index = [Math]::Ceiling($sorted.Count * 0.85) - 1
    $p85 = $sorted[[Math]::Min($p85Index, $sorted.Count - 1)]

    # StdDev
    $sumSquares = ($durations | ForEach-Object { [Math]::Pow($_ - $avg, 2) } | Measure-Object -Sum).Sum
    $stddev = [Math]::Sqrt($sumSquares / $durations.Count)

    $formatted = "~$([Math]::Round($avg))s (avg $([Math]::Round($avg, 1))s, p85 $([Math]::Round($p85, 1))s, range $([Math]::Round($min))-$([Math]::Round($max))s)"

    return @{
        Average   = [Math]::Round($avg, 1)
        P85       = [Math]::Round($p85, 1)
        Min       = [Math]::Round($min, 1)
        Max       = [Math]::Round($max, 1)
        StdDev    = [Math]::Round($stddev, 1)
        Count     = $durations.Count
        Formatted = $formatted
    }
}

function Get-CheckEstimate {
    <#
    .SYNOPSIS
        Reads CI check history and returns per-check estimated durations.

    .PARAMETER FilePath
        Path to the pr-checks.jsonl history file.

    .OUTPUTS
        Hashtable mapping check name to estimated duration in seconds, or $null if <2 entries.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath
    )

    if (-not (Test-Path $FilePath)) { return $null }

    $entries = Get-Content $FilePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Trim() } |
        ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } |
        Where-Object { $_ -ne $null -and $_.checks }

    if ($entries.Count -lt 2) { return $null }

    # Collect per-check durations across all entries
    $checkDurations = @{}
    foreach ($entry in $entries) {
        $checks = $entry.checks
        if ($checks -is [PSCustomObject]) {
            foreach ($prop in $checks.PSObject.Properties) {
                $checkName = $prop.Name
                $checkData = $prop.Value
                if ($checkData.duration_s) {
                    if (-not $checkDurations.ContainsKey($checkName)) {
                        $checkDurations[$checkName] = @()
                    }
                    $checkDurations[$checkName] += [double]$checkData.duration_s
                }
            }
        }
    }

    # Compute averages
    $estimates = @{}
    foreach ($checkName in $checkDurations.Keys) {
        $durations = $checkDurations[$checkName]
        if ($durations.Count -ge 2) {
            $avg = ($durations | Measure-Object -Average).Average
            $estimates[$checkName] = [Math]::Round($avg)
        }
    }

    if ($estimates.Count -eq 0) { return $null }
    return $estimates
}

# ============================================================================
# AI Instructions
# ============================================================================

function Write-AiInstructions {
    <#
    .SYNOPSIS
        Emits failure-type-specific AI instructions.

    .PARAMETER Type
        The type of failure: TestFailure, SonarFailure, BuildFailure, FormatFailure
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("TestFailure", "SonarFailure", "BuildFailure", "FormatFailure")]
        [string]$Type
    )

    $instructions = switch ($Type) {
        "TestFailure" {
            @"

=== AI INSTRUCTIONS ===
Test failures detected. Follow these steps:
1. Fix ALL failing tests, even pre-existing ones (Boy Scout rule)
2. For flaky tests (intermittent failures):
   - Remove race conditions (don't use Thread.Sleep, use proper sync)
   - Don't assert on timing (use completion signals instead)
   - Model after stable tests in the same file
3. For test design issues:
   - No shared mutable state between tests
   - Each test must be independently runnable
   - Use [NotInParallel] only when truly needed
4. Spin up an agent for each independent fix
5. Re-run this script after fixes to verify
========================
"@
        }
        "SonarFailure" {
            @"

=== AI INSTRUCTIONS ===
SonarCloud quality gate failed. Follow these steps:
1. Fix ALL issues, categorized by severity (Blocker > Critical > Major)
2. For code smells:
   - Reduce cognitive complexity (extract methods)
   - Remove duplication (extract shared utilities)
3. For bugs and vulnerabilities:
   - Fix immediately — these are real defects
   - Add tests to prevent regression
4. For coverage gaps:
   - Write tests for uncovered lines
   - Focus on branch coverage, not just line coverage
5. Re-run this script after fixes to verify
========================
"@
        }
        "BuildFailure" {
            @"

=== AI INSTRUCTIONS ===
Build failed. Follow these steps:
1. Read the build error output carefully
2. Fix compilation errors in dependency order
3. Run 'dotnet build' to verify the fix
4. Do NOT skip or suppress warnings without justification
========================
"@
        }
        "FormatFailure" {
            @"

=== AI INSTRUCTIONS ===
Format check failed. Fix:
1. Run: dotnet format
2. Commit the formatting changes
3. Re-run this script to verify
========================
"@
        }
    }

    Write-Host $instructions -ForegroundColor Yellow
}

# ============================================================================
# Utility: Format Duration
# ============================================================================

function Format-Duration {
    <#
    .SYNOPSIS
        Formats a TimeSpan or seconds into a human-readable duration string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [double]$Seconds
    )

    if ($Seconds -lt 60) {
        return "$([Math]::Round($Seconds))s"
    }
    elseif ($Seconds -lt 3600) {
        $m = [Math]::Floor($Seconds / 60)
        $s = [Math]::Round($Seconds % 60)
        return "${m}m${s}s"
    }
    else {
        $h = [Math]::Floor($Seconds / 3600)
        $m = [Math]::Round(($Seconds % 3600) / 60)
        return "${h}h${m}m"
    }
}

# ============================================================================
# Exports
# ============================================================================

Export-ModuleMember -Function @(
    'Write-WhizbangHeader'
    'Initialize-TeeLogging'
    'Write-TeeHost'
    'Stop-TeeLogging'
    'ConvertTo-JsonResult'
    'Write-HistoryEntry'
    'Get-RunEstimate'
    'Get-CheckEstimate'
    'Write-AiInstructions'
    'Format-Duration'
)
