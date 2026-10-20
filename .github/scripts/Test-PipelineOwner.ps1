#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - Pipeline owner" check: a pull request that changes the pipeline, the gates or the
    build inputs merges only with a build admin's say-so.

.DESCRIPTION
    .github/CODEOWNERS gives those files to the build-admins team (GOVERNANCE.md, "Roles"). The rulesets do
    not require a code owner's review: GitHub never lets an author approve their own pull request, so a
    build admin's own pipeline change could not merge without bypassing the rules, which also skipped the
    merge queue. This gate takes that review's place. A pull request that changes any file CODEOWNERS gives
    to build-admins passes only when:
      - its author's repository role is admin (the build-admins team grants it), or
      - a user whose repository role is admin approved the pull request's current head commit, and has not
        since requested changes or had the approval dismissed. An approval of an earlier commit does not
        count, so nothing pushed after the approval merges unreviewed.
    A pull request that changes no such file passes. The role is read, not team membership, which the
    workflow token cannot read.

    CODEOWNERS is read as GitHub reads it: the last matching pattern decides a file's owners. A leading
    slash, or a slash inside the pattern, anchors it at the repository root; a trailing slash makes it a
    directory, matching everything under it; otherwise it names a file (or a directory of that name). A
    pattern with a wildcard is refused rather than guessed at, so a new pattern this script cannot read
    fails the gate instead of owning too few files. A renamed file counts at its old path too.

    Exempt, and reported as exempt: pull requests opened by a bot, as the linked-issue gate exempts them.

    The pull request is read from the API at run time, not from the event that started the run, so an
    approval and a re-run of this job is enough; pr-intake.yml re-runs it when a review is submitted or
    dismissed. In the merge queue the pull request number is read from the queue branch, so the check
    holds for what actually merges.

    Exit 0 on pass, 1 on fail.

.PARAMETER Repository
    owner/name.

.PARAMETER PullRequest
    The pull request number (pull request runs).

.PARAMETER HeadRef
    The queue branch (merge queue runs), gh-readonly-queue/<base>/pr-<number>-<sha>.

.PARAMETER CodeOwnersPath
    The CODEOWNERS file to read. The workflow checks out the base branch's, so a pull request cannot
    loosen it for itself.

.PARAMETER Owner
    The team whose files need a build admin's approval.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [int]$PullRequest = 0,
  [string]$HeadRef = '',
  [string]$CodeOwnersPath = (Join-Path $PSScriptRoot '..' 'CODEOWNERS'),
  [string]$Owner = '@whizbang-lib/build-admins'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GovernanceGuide = 'https://github.com/whizbang-lib/whizbang/blob/develop/GOVERNANCE.md#roles'
# The most changed files the failure message lists before saying how many more there are.
$script:ListedFiles = 20

function ConvertFrom-CodeOwners {
  <# .SYNOPSIS The CODEOWNERS entries in order: Pattern and Owners. Comments and blank lines are skipped. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines)
  @(foreach ($line in $Lines) {
      $text = ($line -replace '(^|\s)#.*$', '').Trim()
      if (-not $text) { continue }
      $parts = @($text -split '\s+')
      [pscustomobject]@{ Pattern = $parts[0]; Owners = @(if ($parts.Count -gt 1) { $parts[1..($parts.Count - 1)] }) }
    })
}

function Test-CodeOwnerMatch {
  <#
  .SYNOPSIS
      Whether a CODEOWNERS pattern matches a repository path. Throws for a wildcard pattern, which this
      script does not read.
  #>
  param([Parameter(Mandatory)][string]$Pattern, [Parameter(Mandatory)][string]$Path)
  if ($Pattern.IndexOfAny([char[]]'*?[') -ge 0) {
    throw "CODEOWNERS pattern '$Pattern' is not supported by Test-PipelineOwner.ps1 (no wildcards); name the directory or file."
  }
  $directory = $Pattern.EndsWith('/')
  $body = $Pattern.Trim('/')
  # A slash at the start or inside anchors the pattern at the root; otherwise it matches at any depth.
  $anchored = $Pattern.StartsWith('/') -or $body.Contains('/')
  $candidates = if ($anchored) { @($Path) } else {
    $segments = $Path -split '/'
    @(for ($i = 0; $i -lt $segments.Count; $i++) { $segments[$i..($segments.Count - 1)] -join '/' })
  }
  foreach ($candidate in $candidates) {
    # A directory owns what is under it, never a file of the same name. A file pattern owns the file, and
    # what is under a directory of that name, as git reads it.
    if ($candidate.StartsWith("$body/", [System.StringComparison]::Ordinal)) { return $true }
    if (-not $directory -and [string]::Equals($candidate, $body, [System.StringComparison]::Ordinal)) { return $true }
  }
  return $false
}

function Get-OwnedFile {
  <# .SYNOPSIS The files, once each in order, whose last matching CODEOWNERS entry names Owner. #>
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$CodeOwners,
    [Parameter(Mandatory)][string]$Owner,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Files
  )
  $owned = [System.Collections.Generic.List[string]]::new()
  foreach ($file in $Files) {
    $last = $null
    foreach ($entry in $CodeOwners) { if (Test-CodeOwnerMatch -Pattern $entry.Pattern -Path $file) { $last = $entry } }
    if ($last -and @($last.Owners) -contains $Owner -and -not $owned.Contains($file)) { $owned.Add($file) }
  }
  $owned.ToArray()
}

function Get-AdminApprover {
  <#
  .SYNOPSIS
      The users with the admin role whose standing review approves the head commit. A reviewer's standing
      review is their latest one that approves, requests changes or was dismissed; a comment changes nothing.
  #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Reviews, [Parameter(Mandatory)][string]$HeadSha)
  $decisive = @($Reviews | Where-Object { $_.State -in @('APPROVED', 'CHANGES_REQUESTED', 'DISMISSED') })
  @($decisive | Group-Object -Property Author | ForEach-Object {
      $standing = $_.Group | Sort-Object -Property SubmittedAt | Select-Object -Last 1
      if ($standing.State -eq 'APPROVED' -and $standing.CommitId -eq $HeadSha -and $standing.AuthorRole -eq 'admin') { $standing.Author }
    })
}

function Get-PipelineOwnerVerdict {
  <#
  .SYNOPSIS
      The decision, with no I/O: Pass, Exempt, Reason, OwnedFiles and the problems found.
  #>
  param(
    [Parameter(Mandatory)]$PullRequest,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$CodeOwners,
    [Parameter(Mandatory)][string]$Owner
  )
  $pr = $PullRequest
  if ($pr.AuthorIsBot) {
    return [pscustomobject]@{ Pass = $true; Exempt = $true; Reason = "opened by a bot ($($pr.Author))"; OwnedFiles = @(); Problems = @() }
  }
  $owned = @(Get-OwnedFile -CodeOwners $CodeOwners -Owner $Owner -Files @($pr.Files))
  if ($owned.Count -eq 0) {
    return [pscustomobject]@{ Pass = $true; Exempt = $false; Reason = 'changes no file build admins own'; OwnedFiles = @(); Problems = @() }
  }
  $verdict = { param([bool]$Pass, [string]$Reason, [string[]]$Problems)
    [pscustomobject]@{ Pass = $Pass; Exempt = $false; Reason = $Reason; OwnedFiles = $owned; Problems = @($Problems) } }
  if ($pr.AuthorRole -eq 'admin') { return & $verdict $true "opened by a build admin ($($pr.Author))" @() }

  $reviews = @($pr.Reviews)
  $approvers = @(Get-AdminApprover -Reviews $reviews -HeadSha $pr.HeadSha)
  if ($approvers.Count -gt 0) { return & $verdict $true "approved on its head commit by build admin $($approvers -join ', ')" @() }

  $listed = ($owned | Select-Object -First $script:ListedFiles | ForEach-Object { "``$_``" }) -join ', '
  if ($owned.Count -gt $script:ListedFiles) { $listed += ", and $($owned.Count - $script:ListedFiles) more" }
  $short = if ($pr.HeadSha.Length -ge 7) { $pr.HeadSha.Substring(0, 7) } else { $pr.HeadSha }
  $stale = @($reviews | Where-Object { $_.State -eq 'APPROVED' -and $_.AuthorRole -eq 'admin' -and $_.CommitId -ne $pr.HeadSha })
  $why = if ($stale.Count -gt 0) { ' A build admin approved an earlier commit only; a commit pushed since needs approving too.' } else { '' }
  $problem = "Pull request #$($pr.Number) changes files that .github/CODEOWNERS gives to build admins: $listed. It merges " +
    "only with a build admin's approval of its current head commit ($short).$why Ask a build admin to review it; this " +
    "check re-runs when they approve (if the CI run is still going then, comment /recheck once it ends). See " +
    """Roles"": $($script:GovernanceGuide)"
  return & $verdict $false 'no build admin approval on the head commit' @($problem)
}

function Get-QueuedPullRequestNumber {
  <# .SYNOPSIS The pull request number a merge queue branch was made for, or 0 for any other branch. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$HeadRef)
  if ($HeadRef -match 'gh-readonly-queue/.+/pr-(\d+)-') { return [int]$Matches[1] }
  return 0
}

function ConvertFrom-PipelineOwnerJson {
  <#
  .SYNOPSIS
      The pull request from the REST responses: the pull request, its changed paths, and its reviews as
      pages (gh api --paginate --slurp). A review whose author account is gone is skipped.
  #>
  param(
    [Parameter(Mandatory)][string]$PullJson,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Files,
    [Parameter(Mandatory)][string]$ReviewsJson
  )
  $pull = $PullJson | ConvertFrom-Json
  $reviews = @(($ReviewsJson | ConvertFrom-Json) | ForEach-Object { $_ } | Where-Object { $_ -and $_.user } | ForEach-Object {
      [pscustomobject]@{ Author = [string]$_.user.login; State = [string]$_.state; CommitId = [string]$_.commit_id
        SubmittedAt = if ($_.submitted_at) { [datetimeoffset]$_.submitted_at } else { [datetimeoffset]::MinValue }; AuthorRole = '' }
    })
  [pscustomobject]@{
    Number      = [int]$pull.number
    Author      = [string]$pull.user.login
    AuthorIsBot = [string]$pull.user.type -eq 'Bot'
    AuthorRole  = ''
    HeadSha     = [string]$pull.head.sha
    Files       = @($Files | Where-Object { $_ })
    Reviews     = $reviews
  }
}

function Set-PipelineOwnerAuthority {
  <# .SYNOPSIS Fills in the author's and each reviewer's repository role, asking RoleOf once per login. #>
  param([Parameter(Mandatory)]$PullRequest, [Parameter(Mandatory)][scriptblock]$RoleOf)
  $known = @{}
  $ask = { param([string]$Login) if (-not $known.ContainsKey($Login)) { $known[$Login] = [string](& $RoleOf $Login) }; $known[$Login] }
  $PullRequest.AuthorRole = & $ask $PullRequest.Author
  foreach ($review in @($PullRequest.Reviews)) { $review.AuthorRole = & $ask $review.Author }
}

function Get-RepositoryRole {
  <#
  .SYNOPSIS
      A user's role on the repository (read, triage, write, maintain, admin), or '' for someone who is not
      a collaborator. Team membership grants the role: committers write, maintainers maintain,
      build-admins admin (GOVERNANCE.md). The same reading as Test-LinkedIssue.ps1.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Login)
  # A user who is not a collaborator reads as 404.
  $role = gh api "repos/$Repository/collaborators/$Login/permission" --jq '.role_name' 2>$null
  if ($LASTEXITCODE -ne 0) { return '' }
  return ($role -join '').Trim()
}

function Read-PipelineOwnerPullRequest {
  <#
  .SYNOPSIS
      The pull request, its changed paths, its reviews and everyone's role, read from the API. Null, with
      an ::error:: line, when it could not be read.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $pull = gh api "repos/$Repository/pulls/$Number"
  if ($LASTEXITCODE -ne 0) { Write-Host "::error::Reading pull request #$Number failed (gh exit $LASTEXITCODE)."; return $null }
  # The old path of a renamed file counts too: moving a file out of an owned directory changes it.
  $files = gh api --paginate "repos/$Repository/pulls/$Number/files?per_page=100" --jq '.[] | .filename, (.previous_filename // empty)'
  if ($LASTEXITCODE -ne 0) { Write-Host "::error::Reading the files of pull request #$Number failed (gh exit $LASTEXITCODE)."; return $null }
  $reviews = gh api --paginate --slurp "repos/$Repository/pulls/$Number/reviews?per_page=100"
  if ($LASTEXITCODE -ne 0) { Write-Host "::error::Reading the reviews of pull request #$Number failed (gh exit $LASTEXITCODE)."; return $null }

  $pr = ConvertFrom-PipelineOwnerJson -PullJson ($pull -join "`n") -Files @($files) -ReviewsJson ($reviews -join "`n")
  Set-PipelineOwnerAuthority -PullRequest $pr -RoleOf { param([string]$Login) Get-RepositoryRole -Repository $Repository -Login $Login }
  return $pr
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$number = if ($PullRequest -gt 0) { $PullRequest } else { Get-QueuedPullRequestNumber -HeadRef $HeadRef }
if ($number -le 0) {
  Write-Output "::error::No pull request to check: pass -PullRequest, or run in the merge queue (head ref '$HeadRef')."
  exit 1
}
if (-not (Test-Path -LiteralPath $CodeOwnersPath)) {
  Write-Output "::error::No CODEOWNERS file at '$CodeOwnersPath', so the owned files cannot be known."
  exit 1
}

$pr = Read-PipelineOwnerPullRequest -Repository $Repository -Number $number
if (-not $pr) { exit 1 }

$codeOwners = ConvertFrom-CodeOwners -Lines @(Get-Content -LiteralPath $CodeOwnersPath)
$verdict = Get-PipelineOwnerVerdict -PullRequest $pr -CodeOwners $codeOwners -Owner $Owner
$summary = if ($verdict.Exempt) { "Pipeline owner: exempt, $($verdict.Reason)." }
  elseif ($verdict.Pass) { "Pipeline owner: pull request #$number $($verdict.Reason)." }
  else { "Pipeline owner: failed, $($verdict.Reason)." }
if ($env:GITHUB_STEP_SUMMARY) {
  $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY
  $verdict.Problems | ForEach-Object { "- $_" } | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
Write-Output $summary
if (-not $verdict.Pass) {
  $verdict.Problems | ForEach-Object { Write-Output "::error::$_" }
  exit 1
}
exit 0
