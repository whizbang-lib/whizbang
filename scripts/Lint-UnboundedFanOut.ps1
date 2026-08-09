#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Flags awaited I/O fan-out (publish / send / dispatch) inside a loop over caller-sized input.

.DESCRIPTION
  Three production incidents in a row shared one shape: a loop that does one awaited durable write
  per element. Each write is individually correct, so unit tests at N=2 pass; at N=500 it becomes
  hundreds of sequential round-trips inside a single handler on a single thread, which starves the
  host's HTTP pipeline until the liveness probe stops being answered and the pod is killed.

  The lesson was already written down once -- StreamIntegrityOptions.MaxCoverageGapReportsPerAudit
  documents exactly this failure -- and the cap was still applied to only one of the two sibling
  paths in the same file. Knowing the rule did not make us apply it. This makes the rule mechanical.

  Statically proving a loop is bounded is not tractable (the bound is often a caller-supplied option
  or an already-capped collection), so this does NOT try. It reports every awaited fan-out inside a
  loop and defers to a baseline of reviewed, known-bounded sites, exactly like Lint-MigrationSql.
  The point is that a NEW one can never appear without someone consciously accepting it.

.PARAMETER SourcePath
  Root to scan. Defaults to src/.

.PARAMETER BaselinePath
  Reviewed, accepted fan-out sites. Defaults to scripts/unbounded-fanout-baseline.txt.

.PARAMETER UpdateBaseline
  Regenerate the baseline from the current findings (run after an intentional, reviewed change).

.EXAMPLE
  pwsh scripts/Lint-UnboundedFanOut.ps1                  # check (CI): exit 1 on any NEW site
  pwsh scripts/Lint-UnboundedFanOut.ps1 -UpdateBaseline  # accept current state as reviewed
#>
param(
  [string]$SourcePath   = (Join-Path $PSScriptRoot '..' 'src'),
  [string]$BaselinePath = (Join-Path $PSScriptRoot 'unbounded-fanout-baseline.txt'),
  [switch]$UpdateBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Awaited calls that cross a process boundary or write durably. A loop doing one of these per
# element is the shape we are hunting; cheap in-memory calls are not.
$fanOutCalls = @(
  'PublishAsync', 'SendAsync', 'DispatchAsync', 'ExecuteAsync',
  'SaveChangesAsync', 'InsertAsync', 'StoreOutboxMessagesAsync', 'StoreInboxMessagesAsync'
)
$callPattern = '(?:' + (($fanOutCalls | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*\('

function Remove-Noise([string]$text) {
  # Crude but sufficient: drop line comments and string literals so their contents cannot match.
  $text = [regex]::Replace($text, '//[^\r\n]*', '')
  $text = [regex]::Replace($text, '@"(?:[^"]|"")*"', '""')
  $text = [regex]::Replace($text, '"(?:\\.|[^"\\])*"', '""')
  return $text
}

$files = Get-ChildItem -Path $SourcePath -Recurse -Filter '*.cs' -File |
  Where-Object {
    $p = $_.FullName -replace '\\', '/'
    $p -notmatch '/(obj|bin)/' -and $p -notmatch '/\.whizbang/' -and $_.Name -notmatch '\.g\.cs$'
  }

$findings = New-Object System.Collections.Generic.List[string]

foreach ($file in $files) {
  $raw = Get-Content -Path $file.FullName -Raw
  if ([string]::IsNullOrEmpty($raw)) { continue }
  $src = Remove-Noise $raw

  foreach ($m in [regex]::Matches($src, '\b(foreach|for|while)\s*\(')) {
    # Walk from the loop header to its opening brace, then to the matching close.
    $i = $m.Index + $m.Length
    $depth = 1
    while ($i -lt $src.Length -and $depth -gt 0) {
      if ($src[$i] -eq '(') { $depth++ }
      elseif ($src[$i] -eq ')') { $depth-- }
      $i++
    }
    while ($i -lt $src.Length -and $src[$i] -match '\s') { $i++ }
    if ($i -ge $src.Length -or $src[$i] -ne '{') { continue }   # single-statement body: too small to fan out

    $start = $i
    $depth = 0
    while ($i -lt $src.Length) {
      if ($src[$i] -eq '{') { $depth++ }
      elseif ($src[$i] -eq '}') { $depth--; if ($depth -eq 0) { break } }
      $i++
    }
    $body = $src.Substring($start, [Math]::Min($i - $start + 1, $src.Length - $start))

    $call = [regex]::Match($body, 'await[^;]{0,200}?' + $callPattern)
    if (-not $call.Success) { continue }

    $line = ($src.Substring(0, $m.Index) -split "`n").Count
    $method = [regex]::Match($call.Value, $callPattern).Value.TrimEnd('(', ' ')
    $rel = (Resolve-Path -Relative $file.FullName) -replace '\\', '/' -replace '^\./', ''
    $findings.Add("$rel : $($m.Groups[1].Value) loop awaits $method")
  }
}

$current = $findings | Sort-Object -Unique

if ($UpdateBaseline) {
  $header = @(
    '# Whizbang unbounded fan-out lint baseline.',
    '# Each entry is an awaited publish/send/write inside a loop that has been REVIEWED and is',
    '# either bounded by construction or explicitly capped. Adding a line here is a decision:',
    '# it asserts the loop cannot grow with caller-supplied input. Removing one is always safe.',
    '# Regenerate: pwsh scripts/Lint-UnboundedFanOut.ps1 -UpdateBaseline'
  )
  Set-Content -Path $BaselinePath -Value ($header + $current)
  Write-Host "baseline updated — $(@($current).Count) reviewed fan-out site(s)."
  exit 0
}

$baseline = @()
if (Test-Path $BaselinePath) {
  $baseline = Get-Content -Path $BaselinePath | Where-Object { $_ -and -not $_.StartsWith('#') }
}

# @() guards: a single-element pipeline result is a scalar under StrictMode and has no .Count.
$new   = @($current | Where-Object { $baseline -notcontains $_ })
$fixed = @($baseline | Where-Object { $current -notcontains $_ })
$currentCount = @($current).Count

if ($new.Count -gt 0) {
  Write-Host "unbounded fan-out lint FAILED — $($new.Count) NEW awaited fan-out site(s) inside a loop:`n"
  $new | ForEach-Object { Write-Host "  $_" }
  Write-Host ""
  Write-Host "  Each iteration performs a durable write or a network call. Confirm the loop is"
  Write-Host "  bounded by something the CALLER cannot inflate, and that a test asserts that bound"
  Write-Host "  at realistic N -- not at N=2. If it is genuinely bounded, accept it explicitly:"
  Write-Host "      pwsh scripts/Lint-UnboundedFanOut.ps1 -UpdateBaseline"
  exit 1
}

if ($fixed.Count -gt 0) {
  Write-Host "unbounded fan-out lint OK — $currentCount baselined; $($fixed.Count) baselined site(s) are gone."
  Write-Host "  Ratchet the baseline down so they cannot come back:"
  Write-Host "      pwsh scripts/Lint-UnboundedFanOut.ps1 -UpdateBaseline"
  exit 0
}

Write-Host "unbounded fan-out lint OK — $currentCount reviewed site(s), 0 new."
exit 0
