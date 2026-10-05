#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - DCO sign-off" check: every commit a pull request adds must carry a
    Developer Certificate of Origin sign-off from its author.

.DESCRIPTION
    A sign-off is a "Signed-off-by: Name <email>" line at the start of a line in the commit message,
    whose email matches the commit's author (case-insensitive). `git commit -s` writes it. The text being
    certified is the DCO 1.1, https://developercertificate.org, adopted in CONTRIBUTING.md.

    Skipped, and counted as skipped:
      - merge commits (more than one parent): GitHub and the merge queue create them;
      - commits by bot accounts (name or noreply email ending in [bot]): Dependabot and the release
        and sync automation;
      - commits authored before the DCO was adopted, so open work and release PRs that carry older
        history are not stranded.

    The commits come from `git log <base>..<head>` over a full clone rather than the pull request API,
    which stops at 250 commits and would leave a long release PR partly unchecked without saying so.
    An empty range fails: it means the history was not fetched, not that there is nothing to check.

    Exit 0 on pass, 1 on fail.

.PARAMETER Base
    The pull request's base commit (github.event.pull_request.base.sha).

.PARAMETER Head
    The pull request's head commit (github.event.pull_request.head.sha).

.EXAMPLE
    pwsh .github/scripts/Test-DcoSignoff.ps1 -Base "$BASE_SHA" -Head "$HEAD_SHA"
#>

param(
    [Parameter(Mandatory)]
    [string]$Base,

    [Parameter(Mandatory)]
    [string]$Head
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The day the project adopted the DCO (the day after the check landed, so nothing already on develop is
# caught). Commits authored earlier are not required to be signed off.
$script:DcoAdoptedOn = [datetimeoffset]'2026-10-06T00:00:00Z'

$script:FieldSeparator = [char]0x1f
$script:RecordSeparator = [char]0x1e

# Turns `git log --format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%B%x1e` output into commit objects.
function ConvertFrom-GitLog {
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Raw)
  foreach ($record in $Raw.Split($script:RecordSeparator)) {
    if ([string]::IsNullOrWhiteSpace($record)) { continue }
    $fields = $record.TrimStart("`r", "`n").Split($script:FieldSeparator, 6)
    [pscustomobject]@{
      Sha         = $fields[0]
      Parents     = @($fields[1].Split(' ', [StringSplitOptions]::RemoveEmptyEntries)).Count
      AuthorName  = $fields[2]
      AuthorEmail = $fields[3]
      AuthorDate  = [datetimeoffset]::Parse($fields[4], [Globalization.CultureInfo]::InvariantCulture)
      Message     = $fields[5].TrimEnd()
    }
  }
}

function Test-IsBot {
  param([pscustomobject]$Commit)
  return $Commit.AuthorName -like '*`[bot`]' -or $Commit.AuthorEmail -like '*`[bot`]@users.noreply.github.com'
}

<#
    The decision, with no I/O. Returns Pass, Checked, Skipped and the list of problems found, one per
    failing commit, each naming the commit and what is wrong with it.
#>
function Get-DcoVerdict {
  param(
      [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Commits,
      [Parameter(Mandatory)][datetimeoffset]$AdoptedOn
  )
  $problems = [System.Collections.Generic.List[string]]::new()
  $checked = 0; $skipped = 0

  if ($Commits.Count -eq 0) {
    $problems.Add('no commits were read from the pull request range; the history was not fetched, so nothing was checked')
  }

  foreach ($commit in $Commits) {
    if ($commit.Parents -gt 1 -or (Test-IsBot $commit) -or $commit.AuthorDate -lt $AdoptedOn) { $skipped++; continue }
    $checked++

    $subject = ($commit.Message -split "`n", 2)[0]
    $label = "$($commit.Sha.Substring(0, 12)) $subject"
    $signoffs = @([regex]::Matches($commit.Message, '(?im)^signed-off-by:\s*(?<name>.+?)\s*<(?<email>[^>]+)>\s*$'))
    if ($signoffs.Count -eq 0) {
      $problems.Add("${label}: no Signed-off-by line")
    }
    elseif (-not ($signoffs | Where-Object { $_.Groups['email'].Value -ieq $commit.AuthorEmail })) {
      $found = ($signoffs | ForEach-Object { $_.Groups['email'].Value }) -join ', '
      $problems.Add("${label}: Signed-off-by $found does not match the author, $($commit.AuthorEmail)")
    }
  }

  return [pscustomobject]@{ Pass = ($problems.Count -eq 0); Checked = $checked; Skipped = $skipped; Problems = @($problems) }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$raw = git log --format="%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%B%x1e" "$Base..$Head"
if ($LASTEXITCODE -ne 0) { Write-Output "::error::git log $Base..$Head failed (exit $LASTEXITCODE)"; exit 1 }
$commits = @(ConvertFrom-GitLog -Raw (($raw | Out-String)))

$verdict = Get-DcoVerdict -Commits $commits -AdoptedOn $script:DcoAdoptedOn
$summary = "DCO sign-off: $($verdict.Checked) commit(s) checked, $($verdict.Skipped) skipped (merges, bots, before adoption)."
if ($env:GITHUB_STEP_SUMMARY) {
  $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY
  $verdict.Problems | ForEach-Object { "- $_" } | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
Write-Output $summary
if (-not $verdict.Pass) {
  $verdict.Problems | ForEach-Object { Write-Output "::error::$_" }
  Write-Output 'To fix: sign off each commit with your author email, then force-push:'
  Write-Output '  git rebase --signoff <base branch>    (all commits on the branch)'
  Write-Output '  git commit --amend --signoff --no-edit (the last commit only)'
  Write-Output 'See CONTRIBUTING.md, "Developer Certificate of Origin".'
  exit 1
}
exit 0
