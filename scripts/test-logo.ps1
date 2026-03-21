#!/usr/bin/env pwsh
# Whizbang ASCII art logo - true color via ANSI escape codes
# Run: pwsh scripts/test-logo.ps1

$esc = [char]27
# Dark navy background matching the logo
$bg = "${esc}[48;2;45;55;72m"
$reset = "${esc}[0m"

# Write a character with true RGB color on dark background
function Write-RGB {
    param([string]$Text, [int]$R, [int]$G, [int]$B)
    Write-Host "${bg}${esc}[38;2;${R};${G};${B}m${Text}${reset}" -NoNewline
}

# Write a segment with a single color
# When writing background-colored spaces, randomly sprinkle dim stars
function Write-Seg {
    param([string]$Text, [int]$R, [int]$G, [int]$B)
    $isBg = ($R -eq 45 -and $G -eq 55 -and $B -eq 72)
    $starChars = @('.', '·', '∙', '*', '⋅', '✦')
    foreach ($ch in $Text.ToCharArray()) {
        if ($isBg -and $ch -eq ' ' -and (Get-Random -Minimum 0 -Maximum 12) -eq 0) {
            # Random dim star
            $brightness = Get-Random -Minimum 220 -Maximum 255
            $starCh = $starChars[(Get-Random -Minimum 0 -Maximum $starChars.Count)]
            Write-RGB $starCh $brightness ($brightness + 10) ($brightness + 20)
        } else {
            Write-RGB $ch $R $G $B
        }
    }
}

# End a line (with background fill to edge)
function Write-EOL {
    # Pad to consistent width with background
    Write-Host "${bg}  ${reset}"
}

Write-Host ""

# Background line above
Write-Seg "                                                                                    " 45 55 72
Write-EOL

# Line 1
Write-Seg "  " 45 55 72
Write-Seg "Φ" 70 158 174; Write-Seg "▌" 56 155 181; Write-Seg "▌     " 57 144 176
Write-Seg ",▄▄" 108 101 131
Write-Seg "         " 45 55 72
Write-Seg "▌▌" 190 60 105; Write-Seg "H" 154 100 108
Write-Seg "      " 45 55 72
Write-Seg "╒" 144 126 110; Write-Seg "██" 234 124 16; Write-Seg "⌐" 148 129 106
Write-Seg "         " 45 55 72
Write-Seg "▓▓" 150 152 154; Write-Seg "L" 165 167 169
Write-Seg "                                     " 45 55 72
Write-EOL

# Line 2
Write-Seg "  " 45 55 72
Write-Seg " " 45 55 72
Write-Seg "██" 19 161 206; Write-Seg "W" 94 128 148; Write-Seg "   " 45 55 72
Write-Seg "█████" 66 52 143
Write-Seg "    " 45 55 72
Write-Seg "▄▄" 173 70 133; Write-Seg "m " 161 92 125
Write-Seg "▓█" 210 42 88; Write-Seg "▄▄▌▌▄" 175 90 70
Write-Seg "   " 45 55 72
Write-Seg "▄▄" 186 131 66
Write-Seg "  " 45 55 72
Write-Seg "▄▄▄▄▄▄" 181 146 71; Write-Seg "╕" 158 138 95
Write-Seg " " 45 55 72
Write-Seg "██▌▌▌▌▄" 140 142 144; Write-Seg "_" 170 170 170
Write-Seg "   " 45 55 72
Write-Seg ",▄▌▌▄▄▄⌐" 155 157 159
Write-Seg " " 45 55 72
Write-Seg "╔▄▄▄▌▌▄" 155 157 159
Write-Seg "    " 45 55 72
Write-Seg "²▌▌▌▄▄▄" 150 152 154
Write-Seg "  " 45 55 72
Write-EOL

# Line 3
Write-Seg "  " 45 55 72
Write-Seg " " 45 55 72
Write-Seg "▀" 53 142 178; Write-Seg "██" 24 131 191
Write-Seg "  " 45 55 72
Write-Seg "▌" 80 89 141; Write-Seg "██" 45 45 143
Write-Seg " " 45 55 72
Write-Seg "╟" 115 84 134; Write-Seg "██" 121 36 141
Write-Seg "  " 45 55 72
Write-Seg "▐▓▓" 156 90 131
Write-Seg "  " 45 55 72
Write-Seg "▓█▓" 208 43 62; Write-Seg '"' 160 101 94; Write-Seg "'" 157 110 97
Write-Seg "▀██" 195 100 55
Write-Seg "  " 45 55 72
Write-Seg "██" 239 130 11
Write-Seg "  " 45 55 72
Write-Seg '""' 172 143 80; Write-Seg "╠▓▓" 187 165 64; Write-Seg "▀" 213 157 36
Write-Seg "  " 45 55 72
Write-Seg "███" 140 142 144; Write-Seg "╙" 160 162 164; Write-Seg '"' 165 167 169; Write-Seg "╨██" 135 137 139
Write-Seg "╕" 165 167 169
Write-Seg "▄██▀" 138 140 142; Write-Seg "╙╙" 165 167 169; Write-Seg "▀██" 130 132 134; Write-Seg "M" 160 162 164
Write-Seg " " 45 55 72
Write-Seg "▓██▀" 145 147 149; Write-Seg "²" 165 167 169; Write-Seg "▀██" 130 132 134
Write-Seg " " 45 55 72
Write-Seg "┌██▀" 170 172 174; Write-Seg '"' 165 167 169; Write-Seg "╙▓██" 145 147 149
Write-Seg "  " 45 55 72
Write-EOL

# Line 4
Write-Seg "  " 45 55 72
Write-Seg "  " 45 55 72
Write-Seg "██" 27 102 180; Write-Seg "▄▄" 97 113 140; Write-Seg "██" 42 54 147
Write-Seg "   " 45 55 72
Write-Seg "██▌" 132 56 137; Write-Seg "_" 132 122 128; Write-Seg "▓▓" 205 26 137; Write-Seg "Ñ" 181 71 123
Write-Seg "  " 45 55 72
Write-Seg "▓█" 206 44 55; Write-Seg "H" 165 98 89
Write-Seg "   " 45 55 72
Write-Seg "██" 239 103 12
Write-Seg "  " 45 55 72
Write-Seg "██" 239 143 10
Write-Seg "  " 45 55 72
Write-Seg "_" 137 131 117; Write-Seg "Φ▓▌" 199 166 52
Write-Seg "    " 45 55 72
Write-Seg "██▌" 140 142 144
Write-Seg "   " 45 55 72
Write-Seg "▄▓██▌▓▄" 143 145 147
Write-Seg "   " 45 55 72
Write-Seg "██M" 138 140 142
Write-Seg " " 45 55 72
Write-Seg "╫▓▌" 155 157 159
Write-Seg "   " 45 55 72
Write-Seg "██" 130 132 134
Write-Seg " " 45 55 72
Write-Seg "▐██" 160 162 164
Write-Seg "   " 45 55 72
Write-Seg "╓██" 170 172 174
Write-Seg "  " 45 55 72
Write-EOL

# Line 5
Write-Seg "  " 45 55 72
Write-Seg "  " 45 55 72
Write-Seg "╙" 99 122 142; Write-Seg "████" 35 81 157
Write-Seg "     " 45 55 72
Write-Seg "▀██▓▀" 152 49 137
Write-Seg "   " 45 55 72
Write-Seg "▓█" 204 47 51; Write-Seg "M" 167 98 87
Write-Seg "   " 45 55 72
Write-Seg "██" 239 108 12
Write-Seg "  " 45 55 72
Write-Seg "██" 239 148 10
Write-Seg " " 45 55 72
Write-Seg "▐▓▓▓▓▓▓▌" 200 180 80
Write-Seg " " 45 55 72
Write-Seg "███████▌" 140 142 144; Write-Seg '"' 165 167 169
Write-Seg " " 45 55 72
Write-Seg "'" 170 172 174; Write-Seg "▓██████" 143 145 147; Write-Seg "M" 160 162 164
Write-Seg " " 45 55 72
Write-Seg "▓█▌" 210 212 214
Write-Seg "   " 45 55 72
Write-Seg "██" 130 132 134
Write-Seg "  " 45 55 72
Write-Seg "╨███████" 138 140 142
Write-Seg "  " 45 55 72
Write-EOL

# Line 6: g descender
Write-Seg "                                                                          " 45 55 72
Write-Seg "▓█▌▄▄▓█▌" 150 152 154
Write-Seg "  " 45 55 72
Write-EOL

# Background line below
Write-Seg "                                                                                    " 45 55 72
Write-EOL

# W! - https://whizba.ng/ tagline
Write-Seg "                                " 45 55 72
Write-Seg "W! - https://whizba.ng/" 200 210 220
Write-Seg "                             " 45 55 72
Write-EOL

Write-Host ""

# Header box
Write-Host "  ╔══════════════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║  Test Runner v1.0                                                                ║" -ForegroundColor Cyan
Write-Host "  ║  Mode: AiUnit | Coverage: On | FailFast: On                                      ║" -ForegroundColor Cyan
Write-Host "  ╠══════════════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Cyan
Write-Host "  ║  Estimated: ~50s (avg 48.3s, p85 55.1s)                                          ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
