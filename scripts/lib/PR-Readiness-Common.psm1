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

# True-color ASCII art banner rendered with ANSI escape codes.
# Uses the actual Whizbang logo colors extracted from the brand assets.
# Dark navy background (#2d3748) with gradient-colored foreground characters.

$script:Esc = [char]27
$script:BgColor = "${script:Esc}[48;2;45;55;72m"
$script:Reset = "${script:Esc}[0m"

$script:StarChars = @('.', '·', '∙', '*', '⋅', '✦')

function Write-LogoSeg {
    <# Write a segment of text with true RGB foreground on dark background.
       Background spaces randomly get bright star characters. #>
    param([string]$Text, [int]$R, [int]$G, [int]$B)
    $isBg = ($R -eq 45 -and $G -eq 55 -and $B -eq 72)
    foreach ($ch in $Text.ToCharArray()) {
        if ($isBg -and $ch -eq ' ' -and (Get-Random -Minimum 0 -Maximum 12) -eq 0) {
            $brightness = Get-Random -Minimum 220 -Maximum 255
            $starCh = $script:StarChars[(Get-Random -Minimum 0 -Maximum $script:StarChars.Count)]
            Write-Host "${script:BgColor}${script:Esc}[38;2;${brightness};$($brightness + 5);$($brightness + 10)m${starCh}${script:Reset}" -NoNewline
        } else {
            Write-Host "${script:BgColor}${script:Esc}[38;2;${R};${G};${B}m${ch}${script:Reset}" -NoNewline
        }
    }
}

function Write-LogoEOL {
    Write-Host "${script:BgColor}  ${script:Reset}"
}

function Write-BannerBody {
    <# Renders the 9 banner lines (called for initial draw and animation frames) #>

    # Background line above
    Write-LogoSeg "                                                                                    " 45 55 72
    Write-LogoEOL

    # Line 1
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "Φ" 70 158 174; Write-LogoSeg "▌" 56 155 181; Write-LogoSeg "▌     " 57 144 176
    Write-LogoSeg ",▄▄" 108 101 131
    Write-LogoSeg "         " 45 55 72
    Write-LogoSeg "▌▌" 190 60 105; Write-LogoSeg "H" 154 100 108
    Write-LogoSeg "      " 45 55 72
    Write-LogoSeg "╒" 144 126 110; Write-LogoSeg "██" 234 124 16; Write-LogoSeg "⌐" 148 129 106
    Write-LogoSeg "         " 45 55 72
    Write-LogoSeg "▓▓" 150 152 154; Write-LogoSeg "L" 165 167 169
    Write-LogoSeg "                                     " 45 55 72
    Write-LogoEOL

    # Line 2
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "██" 19 161 206; Write-LogoSeg "W" 94 128 148; Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "█████" 66 52 143
    Write-LogoSeg "    " 45 55 72
    Write-LogoSeg "▄▄" 173 70 133; Write-LogoSeg "m " 161 92 125
    Write-LogoSeg "▓█" 210 42 88; Write-LogoSeg "▄▄▌▌▄" 175 90 70
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "▄▄" 186 131 66
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "▄▄▄▄▄▄" 181 146 71; Write-LogoSeg "╕" 158 138 95
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "██▌▌▌▌▄" 140 142 144; Write-LogoSeg "_" 170 170 170
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg ",▄▌▌▄▄▄⌐" 155 157 159
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "╔▄▄▄▌▌▄" 155 157 159
    Write-LogoSeg "    " 45 55 72
    Write-LogoSeg "²▌▌▌▄▄▄" 150 152 154
    Write-LogoSeg "  " 45 55 72
    Write-LogoEOL

    # Line 3
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "▀" 53 142 178; Write-LogoSeg "██" 24 131 191
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "▌" 80 89 141; Write-LogoSeg "██" 45 45 143
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "╟" 115 84 134; Write-LogoSeg "██" 121 36 141
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "▐▓▓" 156 90 131
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "▓█▓" 208 43 62; Write-LogoSeg '"' 160 101 94; Write-LogoSeg "'" 157 110 97
    Write-LogoSeg "▀██" 195 100 55
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "██" 239 130 11
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg '""' 172 143 80; Write-LogoSeg "╠▓▓" 187 165 64; Write-LogoSeg "▀" 213 157 36
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "███" 140 142 144; Write-LogoSeg "╙" 160 162 164; Write-LogoSeg '"' 165 167 169; Write-LogoSeg "╨██" 135 137 139
    Write-LogoSeg "╕" 165 167 169
    Write-LogoSeg "▄██▀" 138 140 142; Write-LogoSeg "╙╙" 165 167 169; Write-LogoSeg "▀██" 130 132 134; Write-LogoSeg "M" 160 162 164
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "▓██▀" 145 147 149; Write-LogoSeg "²" 165 167 169; Write-LogoSeg "▀██" 130 132 134
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "┌██▀" 170 172 174; Write-LogoSeg '"' 165 167 169; Write-LogoSeg "╙▓██" 145 147 149
    Write-LogoSeg "  " 45 55 72
    Write-LogoEOL

    # Line 4
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "██" 27 102 180; Write-LogoSeg "▄▄" 97 113 140; Write-LogoSeg "██" 42 54 147
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██▌" 132 56 137; Write-LogoSeg "_" 132 122 128; Write-LogoSeg "▓▓" 205 26 137; Write-LogoSeg "Ñ" 181 71 123
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "▓█" 206 44 55; Write-LogoSeg "H" 165 98 89
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██" 239 103 12
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "██" 239 143 10
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "_" 137 131 117; Write-LogoSeg "Φ▓▌" 199 166 52
    Write-LogoSeg "    " 45 55 72
    Write-LogoSeg "██▌" 140 142 144
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "▄▓██▌▓▄" 143 145 147
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██M" 138 140 142
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "╫▓▌" 155 157 159
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██" 130 132 134
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "▐██" 160 162 164
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "╓██" 170 172 174
    Write-LogoSeg "  " 45 55 72
    Write-LogoEOL

    # Line 5
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "╙" 99 122 142; Write-LogoSeg "████" 35 81 157
    Write-LogoSeg "     " 45 55 72
    Write-LogoSeg "▀██▓▀" 152 49 137
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "▓█" 204 47 51; Write-LogoSeg "M" 167 98 87
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██" 239 108 12
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "██" 239 148 10
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "▐▓▓▓▓▓▓▌" 200 180 80
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "███████▌" 140 142 144; Write-LogoSeg '"' 165 167 169
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "'" 170 172 174; Write-LogoSeg "▓██████" 143 145 147; Write-LogoSeg "M" 160 162 164
    Write-LogoSeg " " 45 55 72
    Write-LogoSeg "▓█▌" 210 212 214
    Write-LogoSeg "   " 45 55 72
    Write-LogoSeg "██" 130 132 134
    Write-LogoSeg "  " 45 55 72
    Write-LogoSeg "╨███████" 138 140 142
    Write-LogoSeg "  " 45 55 72
    Write-LogoEOL

    # Line 6: g descender
    Write-LogoSeg "                                                                          " 45 55 72
    Write-LogoSeg "▓█▌▄▄▓█▌" 150 152 154
    Write-LogoSeg "  " 45 55 72
    Write-LogoEOL

    # Background line below
    Write-LogoSeg "                                                                                    " 45 55 72
    Write-LogoEOL

    # W! - https://whizba.ng/ tagline
    Write-LogoSeg "                                " 45 55 72
    Write-LogoSeg "W! - https://whizba.ng/" 200 210 220
    Write-LogoSeg "                             " 45 55 72
    Write-LogoEOL

}

function Write-WhizbangBanner {
    <#
    .SYNOPSIS
        Prints the Whizbang ASCII art banner with true-color gradient, random stars, and optional twinkle animation.
    .PARAMETER Animate
        When true, redraws the banner 3 times for a twinkling star effect. Default: true.
    #>
    [CmdletBinding()]
    param([bool]$Animate = $true)

    Write-Host ""

    # Render the banner and record which buffer rows it occupies
    $bannerStartRow = [Console]::CursorTop
    Write-BannerBody
    $bannerEndRow = [Console]::CursorTop

    Write-Host ""

    # Start async star twinkle animation in a background runspace
    # The thread writes directly to the console buffer using [Console] APIs
    # which are thread-safe. It only touches the banner's rows.
    if ($Animate) {
        $script:TwinkleRunspace = [runspacefactory]::CreateRunspace()
        $script:TwinkleRunspace.Open()

        $script:TwinklePipeline = $script:TwinkleRunspace.CreatePipeline()
        $script:TwinklePipeline.Commands.AddScript(@"
            `$esc = [char]27
            `$bg = "`$esc[48;2;45;55;72m"
            `$reset = "`$esc[0m"
            `$starChars = @('.', '·', '∙', '*', '⋅', '✦')
            `$startRow = $bannerStartRow
            `$endRow = $bannerEndRow
            `$random = [System.Random]::new()

            # Build a map of background positions by scanning each banner row
            # We know the banner width is ~86 chars and art characters are non-space
            # Rather than track exact positions, randomly pick positions in the banner area
            `$width = [Console]::BufferWidth

            for (`$frame = 0; `$frame -lt 5; `$frame++) {
                Start-Sleep -Seconds 1

                # Pick ~30 random positions in the banner area to toggle stars
                for (`$i = 0; `$i -lt 30; `$i++) {
                    `$row = `$random.Next(`$startRow, `$endRow)
                    `$col = `$random.Next(0, [Math]::Min(86, `$width))

                    try {
                        # Save main cursor, move to position, write, restore
                        `$savedTop = [Console]::CursorTop
                        `$savedLeft = [Console]::CursorLeft

                        [Console]::SetCursorPosition(`$col, `$row)

                        # Read what's there — if it's a space or star char, we can replace it
                        # We can't read the buffer easily, so just write a star or space
                        if (`$random.Next(3) -eq 0) {
                            `$brightness = `$random.Next(220, 256)
                            `$star = `$starChars[`$random.Next(`$starChars.Length)]
                            [Console]::Write("`$bg`$esc[38;2;`$brightness;`$(`$brightness+5);`$(`$brightness+10)m`$star`$reset")
                        } else {
                            [Console]::Write("`$bg`$esc[38;2;45;55;72m `$reset")
                        }

                        [Console]::SetCursorPosition(`$savedLeft, `$savedTop)
                    } catch {
                        # Cursor position may be invalid if terminal resized or scrolled
                    }
                }
            }
"@)
        $script:TwinklePipeline.InvokeAsync()
    }
}

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

    .PARAMETER Details
        Optional array of detail strings to display as additional lines in the config box.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptName,

        [string]$Version = "1.0",

        [hashtable]$Params = @{},

        [string]$Estimate = "",

        [string[]]$Details = @()
    )

    # Print the ASCII art banner
    Write-WhizbangBanner

    # Build config line
    $configParts = @()
    foreach ($key in $Params.Keys | Sort-Object) {
        $configParts += "$key`: $($Params[$key])"
    }
    $configLine = $configParts -join " | "

    # Calculate box width to match the logo banner width (~86 chars)
    $titleLine = "  $ScriptName v$Version"
    $bannerWidth = 84
    $innerWidth = $bannerWidth - 4  # account for "  ╔" and "╗" borders

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

    foreach ($detail in $Details) {
        Write-Host "  ║" -ForegroundColor Cyan -NoNewline
        Write-Host "$("  $detail".PadRight($innerWidth))" -ForegroundColor Gray -NoNewline
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

# Schema version for JSONL history entries (increment ONLY when entry format changes).
# This is NOT the script version — it controls data compatibility filtering.
$script:HistorySchemaVersion = 1

# Script version (tracks feature changes, shown in headers)
$script:ScriptVersion = "1.0.0"

# AI attention marker — prefix on any line that AI agents should parse/act on.
# AI consumers can filter output with: grep "🤖"
$script:AI = "🤖"

function Write-AiLine {
    <#
    .SYNOPSIS
        Writes a line prefixed with the AI attention marker (🤖).
        AI agents can filter script output by grepping for this marker.

    .PARAMETER Message
        The message to write.

    .PARAMETER ForegroundColor
        Console foreground color. Default: Yellow.

    .PARAMETER Indent
        Optional indent prefix.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [string]$ForegroundColor = "Yellow",

        [string]$Indent = ""
    )

    Write-Host "${Indent}${script:AI} ${Message}" -ForegroundColor $ForegroundColor
}

function Write-HistoryEntry {
    <#
    .SYNOPSIS
        Appends a JSONL line to the specified history file.

    .PARAMETER FilePath
        Path to the JSONL history file (e.g., logs/test-runs.jsonl).

    .PARAMETER Entry
        Hashtable containing the entry data. Timestamp and schema version are added automatically.
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

    # Add metadata if not present
    if (-not $Entry.ContainsKey("timestamp")) {
        $Entry["timestamp"] = (Get-Date).ToUniversalTime().ToString("o")
    }
    if (-not $Entry.ContainsKey("v")) {
        $Entry["v"] = $script:HistorySchemaVersion
    }
    if (-not $Entry.ContainsKey("script_version")) {
        $Entry["script_version"] = $script:ScriptVersion
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

    $entries = @(Get-Content $FilePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Trim() } |
        ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } |
        Where-Object { $_ -ne $null -and ($_.v -eq $script:HistorySchemaVersion -or -not $_.v) })

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

    $entries = @(Get-Content $FilePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Trim() } |
        ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } |
        Where-Object { $_ -ne $null -and $_.checks -and ($_.v -eq $script:HistorySchemaVersion -or -not $_.v) })

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

    .PARAMETER Indent
        Optional indent prefix for each line (e.g., "    " when called from a child script).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("TestFailure", "SonarFailure", "BuildFailure", "FormatFailure")]
        [string]$Type,

        [string]$Indent = ""
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

    $instructions -split "`n" | Where-Object { $_.Trim() } | ForEach-Object {
        Write-Host "${Indent}${script:AI} $_" -ForegroundColor Yellow
    }
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
# Cleanup
# ============================================================================

function Invoke-CleanLogs {
    <#
    .SYNOPSIS
        Removes log files and coverage reports.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $logsDir = Join-Path $RepoRoot "logs"
    $coverageReportDir = Join-Path $RepoRoot "coverage-report"

    $cleaned = @()

    # Clean log files (pr-run-*.log, pr-unit-tests.log, pr-integration-tests.log)
    if (Test-Path $logsDir) {
        $logFiles = Get-ChildItem -Path $logsDir -Filter "*.log" -ErrorAction SilentlyContinue
        if ($logFiles) {
            $logFiles | Remove-Item -Force
            $cleaned += "$($logFiles.Count) log file(s)"
        }
    }

    # Clean coverage reports (HTML reports from reportgenerator)
    if (Test-Path $coverageReportDir) {
        Remove-Item -Path $coverageReportDir -Recurse -Force
        $cleaned += "coverage-report/"
    }

    # Clean cobertura XML files from test output
    $coberturaFiles = Get-ChildItem -Path (Join-Path $RepoRoot "tests") -Filter "*.cobertura.xml" -Recurse -ErrorAction SilentlyContinue
    if ($coberturaFiles) {
        $coberturaFiles | Remove-Item -Force
        $cleaned += "$($coberturaFiles.Count) cobertura XML file(s)"
    }

    if ($cleaned.Count -gt 0) {
        Write-Host "  Cleaned: $($cleaned -join ', ')" -ForegroundColor Green
    } else {
        Write-Host "  Nothing to clean" -ForegroundColor Gray
    }
}

function Invoke-CleanMetrics {
    <#
    .SYNOPSIS
        Removes JSONL history/metrics files.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $logsDir = Join-Path $RepoRoot "logs"
    $cleaned = @()

    if (Test-Path $logsDir) {
        $jsonlFiles = Get-ChildItem -Path $logsDir -Filter "*.jsonl" -ErrorAction SilentlyContinue
        if ($jsonlFiles) {
            $jsonlFiles | Remove-Item -Force
            $cleaned += "$($jsonlFiles.Count) metrics file(s)"
        }
    }

    if ($cleaned.Count -gt 0) {
        Write-Host "  Cleaned: $($cleaned -join ', ')" -ForegroundColor Green
    } else {
        Write-Host "  Nothing to clean" -ForegroundColor Gray
    }
}

function Invoke-CleanAll {
    <#
    .SYNOPSIS
        Removes all logs, metrics, and coverage reports.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    Invoke-CleanLogs -RepoRoot $RepoRoot
    Invoke-CleanMetrics -RepoRoot $RepoRoot
}

# ============================================================================
# Exports
# ============================================================================

Export-ModuleMember -Function @(
    'Write-WhizbangBanner'
    'Write-WhizbangHeader'
    'Initialize-TeeLogging'
    'Write-TeeHost'
    'Stop-TeeLogging'
    'ConvertTo-JsonResult'
    'Write-HistoryEntry'
    'Get-RunEstimate'
    'Get-CheckEstimate'
    'Write-AiInstructions'
    'Write-AiLine'
    'Format-Duration'
    'Invoke-CleanLogs'
    'Invoke-CleanMetrics'
    'Invoke-CleanAll'
)
