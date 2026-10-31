#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Regenerates the NuGet lock files a Dependabot pull request leaves stale and pushes them back to its
    branch. Run by .github/workflows/dependabot-lockfiles.yml.

.DESCRIPTION
    Dependabot's NuGet updater bumps Directory.Packages.props and the packages.lock.json of each project
    that references the package directly. A project that reaches the package through a project reference
    keeps the old version in its lock file, so CI's locked-mode restore fails with NU1004. Restoring with
    --force-evaluate rewrites every lock file from the current graph; this script does that for the
    solution and for every project with a lock file the solution does not contain, then commits only
    packages.lock.json files.

    The commit is made through the GitHub API (blobs, a tree, a commit, then a fast-forward of the
    branch), never with git commit and git push. GitHub signs a commit an app creates through the API,
    so it shows as verified and passes the develop ruleset's signed-commits rule; a commit made locally
    and pushed is unsigned and blocks the pull request.

    Two steps, so the workflow can stop before it needs a token or an SDK:

      Credentials  Decides whether the GitHub App's Dependabot secrets are set. Writes ready=true|false to
                   GITHUB_OUTPUT. Missing secrets are a notice, never a failure: the workflow ships before
                   the app exists.
      Regenerate   Restores, and when a lock file changed, commits it as the app's bot account with a DCO
                   sign-off on top of the pull request's head and moves its branch there. Skips when the
                   head commit is already the app's (its own update re-triggers the workflow). gh reads
                   the app token from GH_TOKEN; LOCKFILE_TOKEN must hold it too, so a missing token fails
                   before the restore.

    Idempotent: a second restore over the regenerated files changes nothing, so a re-run commits nothing.
    A branch update refused because the branch moved (Dependabot rebased it while this ran) is a notice:
    the run for the new head regenerates again.

    Exit 0 on success or skip, 1 on failure.

.PARAMETER Step
    Credentials or Regenerate.

.PARAMETER HasAppId
    'true' when the LOCKFILE_APP_ID secret is set (Credentials).

.PARAMETER HasPrivateKey
    'true' when the LOCKFILE_APP_PRIVATE_KEY secret is set (Credentials).

.PARAMETER AppSlug
    The GitHub App's slug, from actions/create-github-app-token's app-slug output (Regenerate).

.PARAMETER HeadRef
    The pull request's head branch name (Regenerate).

.PARAMETER HeadSha
    The pull request's head commit, the one checked out (Regenerate).

.PARAMETER Repository
    owner/name (Regenerate).

.PARAMETER Solution
    The solution to restore. Default: Whizbang.slnx.

.EXAMPLE
    pwsh .github/scripts/Update-DependabotLockFiles.ps1 -Step Credentials -HasAppId true -HasPrivateKey false

.EXAMPLE
    pwsh .github/scripts/Update-DependabotLockFiles.ps1 -Step Regenerate -AppSlug whizbang-lockfiles `
        -HeadRef dependabot/nuget/x -HeadSha $sha -Repository whizbang-lib/whizbang
#>

param(
    [Parameter(Mandatory)]
    [ValidateSet('Credentials', 'Regenerate')]
    [string]$Step,

    [Parameter()]
    [string]$HasAppId = '',

    [Parameter()]
    [string]$HasPrivateKey = '',

    [Parameter()]
    [string]$AppSlug = '',

    [Parameter()]
    [string]$HeadRef = '',

    [Parameter()]
    [string]$HeadSha = '',

    [Parameter()]
    [string]$Repository = '',

    [Parameter()]
    [string]$Solution = 'Whizbang.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LockFileName = 'packages.lock.json'
$script:SetupHint = 'See .github/WORKFLOWS.md, "Dependabot lock files", for the GitHub App and its two Dependabot secrets.'

<#
    Whether the app's two secrets are set. Returns Ready and the message to show.
#>
function Get-CredentialVerdict {
  param([string]$HasAppId, [string]$HasPrivateKey)
  $missing = @()
  if ($HasAppId -ne 'true') { $missing += 'LOCKFILE_APP_ID' }
  if ($HasPrivateKey -ne 'true') { $missing += 'LOCKFILE_APP_PRIVATE_KEY' }
  if ($missing.Count -eq 0) {
    return [pscustomobject]@{ Ready = $true; Message = 'The lock-file GitHub App credentials are set.' }
  }
  $names = $missing -join ' and '
  return [pscustomobject]@{
    Ready   = $false
    Message = "Lock files not regenerated: the Dependabot secret(s) $names are not set. $script:SetupHint"
  }
}

<#
    The commit identity of a GitHub App's bot account: "<slug>[bot]" and its users.noreply address,
    which GitHub attributes to the app.
#>
function Get-BotIdentity {
  param([Parameter(Mandatory)][string]$Slug, [Parameter(Mandatory)][string]$UserId)
  $name = "$Slug[bot]"
  return [pscustomobject]@{ Name = $name; Email = "$UserId+$name@users.noreply.github.com" }
}

# The project paths a .slnx lists, with forward slashes.
function Get-SolutionProject {
  param([Parameter(Mandatory)][string]$SlnxXml)
  $xml = [xml]$SlnxXml
  return @($xml.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') } | Where-Object { $_ })
}

<#
    What to restore: the solution, then every project with a committed lock file that the solution does
    not contain (a project restored on its own still restores its project references).
#>
function Get-RestoreTarget {
  param(
      [Parameter(Mandatory)][string]$Solution,
      [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$SolutionProjects,
      [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$LockedProjects
  )
  $inSolution = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($p in $SolutionProjects) { [void]$inSolution.Add($p.Replace('\', '/')) }
  $outside = @($LockedProjects | ForEach-Object { $_.Replace('\', '/') } | Where-Object { -not $inSolution.Contains($_) } | Sort-Object -Unique)
  return @($Solution) + $outside
}

<#
    Splits `git status --porcelain --untracked-files=all` lines into lock files and anything else.
    Restore writes only lock files (and ignored obj/ output); anything else is reported, never committed.
#>
function Get-ChangedFile {
  param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Porcelain)
  $lock = [System.Collections.Generic.List[string]]::new()
  $other = [System.Collections.Generic.List[string]]::new()
  foreach ($line in $Porcelain) {
    if ($line.Length -lt 4) { continue }
    $path = $line.Substring(3).Trim('"')
    if ($path.Contains(' -> ')) { $path = ($path -split ' -> ', 2)[1].Trim('"') }
    if ((Split-Path -Leaf $path) -eq $script:LockFileName) { $lock.Add($path) } else { $other.Add($path) }
  }
  return [pscustomobject]@{ LockFiles = @($lock); Other = @($other) }
}

# The commit message, ending with the DCO sign-off of the app's bot account.
function Get-CommitMessage {
  param([Parameter(Mandatory)][int]$Count, [Parameter(Mandatory)]$Identity)
  return @"
chore(deps): regenerate $Count NuGet lock file(s) for the Dependabot update

Dependabot updates Directory.Packages.props and the lock files of the projects that
reference a package directly. Projects that reach it through a project reference keep
the old version, and a locked-mode restore fails with NU1004. Regenerated with
dotnet restore --force-evaluate by .github/workflows/dependabot-lockfiles.yml.

Signed-off-by: $($Identity.Name) <$($Identity.Email)>
"@
}

<#
    Whether the head commit is the app's own lock-file commit, from `git log -1 --format=%an%x09%ae`.
    GitHub sets the author of an API commit from the token, so either the name or the address matches.
#>
function Test-AppCommit {
  param([Parameter(Mandatory)][AllowEmptyString()][string]$NameAndEmail, [Parameter(Mandatory)]$Identity)
  $name, $email = (([string]$NameAndEmail).Trim() -split "`t", 2) + @('')
  return ($name.Trim() -ieq $Identity.Name) -or ($email.Trim() -ieq $Identity.Email)
}

<#
    Calls the GitHub API through gh with a JSON body read from a file (a lock file in base64 is larger
    than a command-line argument may be). Returns the parsed response, or $null with -AllowFailure.
#>
function Invoke-GitHubApi {
  param(
      [Parameter(Mandatory)][string]$Method,
      [Parameter(Mandatory)][string]$Path,
      [Parameter(Mandatory)]$Body,
      [Parameter(Mandatory)][string]$What,
      [switch]$AllowFailure
  )
  $file = New-TemporaryFile
  try {
    ConvertTo-Json -InputObject $Body -Depth 5 -Compress | Set-Content -Path $file.FullName -NoNewline
    $out = gh api --method $Method $Path --input $file.FullName
    if ($LASTEXITCODE -ne 0 -and $AllowFailure) { return $null }
    Assert-ExitCode $What
  }
  finally { Remove-Item -LiteralPath $file.FullName -ErrorAction SilentlyContinue }
  return (@($out) -join "`n") | ConvertFrom-Json
}

<#
    Creates the lock-file commit on top of HeadSha through the GitHub API: a blob per changed lock file
    (a deleted one is removed from the tree), a tree over HeadSha's, and a commit with no author or
    committer given, so GitHub records the app's bot account and signs it. Returns the commit's sha.
#>
function New-LockFileCommit {
  param(
      [Parameter(Mandatory)][string]$Repository,
      [Parameter(Mandatory)][string]$HeadSha,
      [Parameter(Mandatory)][string[]]$LockFiles,
      [Parameter(Mandatory)][string]$Message
  )
  $baseTree = git rev-parse "$HeadSha^{tree}"
  Assert-ExitCode 'git rev-parse'
  $entries = foreach ($path in $LockFiles) {
    $sha = $null
    if (Test-Path -LiteralPath $path -PathType Leaf) {
      $content = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).Path))
      $blob = Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/blobs" -Body ([ordered]@{ content = $content; encoding = 'base64' }) `
          -What "Uploading $path"
      $sha = $blob.sha
    }
    [ordered]@{ path = $path; mode = '100644'; type = 'blob'; sha = $sha }
  }
  $tree = Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/trees" `
      -Body ([ordered]@{ base_tree = ([string]$baseTree).Trim(); tree = @($entries) }) -What 'Creating the tree'
  $commit = Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/commits" `
      -Body ([ordered]@{ message = $Message; tree = $tree.sha; parents = @($HeadSha) }) -What 'Creating the commit'
  return $commit.sha
}

function Write-StepOutput {
  param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)
  if ($env:GITHUB_OUTPUT) { "$Name=$Value" | Add-Content -Path $env:GITHUB_OUTPUT }
}

function Write-StepSummary {
  param([Parameter(Mandatory)][string]$Text)
  if ($env:GITHUB_STEP_SUMMARY) { $Text | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
  Write-Host $Text
}

# Fails the step when the last native command did.
function Assert-ExitCode {
  param([Parameter(Mandatory)][string]$What)
  if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# The csproj beside every tracked lock file, relative to the repository root.
function Get-LockedProject {
  $files = @(git ls-files -- "*$script:LockFileName" "**/$script:LockFileName")
  Assert-ExitCode 'git ls-files'
  foreach ($file in ($files | Sort-Object -Unique)) {
    $dir = Split-Path -Parent $file
    $project = Get-ChildItem -Path $(if ($dir) { $dir } else { '.' }) -Filter '*.csproj' -File | Select-Object -First 1
    if ($project -and $dir) { "$($dir.Replace('\', '/'))/$($project.Name)" }
    elseif ($project) { $project.Name }
  }
}

function Invoke-CredentialStep {
  param([string]$HasAppId, [string]$HasPrivateKey)
  $verdict = Get-CredentialVerdict -HasAppId $HasAppId -HasPrivateKey $HasPrivateKey
  Write-StepOutput -Name 'ready' -Value $verdict.Ready.ToString().ToLowerInvariant()
  if ($verdict.Ready) { Write-Host $verdict.Message }
  else {
    Write-Host "::notice title=Dependabot lock files::$($verdict.Message)"
    Write-StepSummary -Text $verdict.Message
  }
}

function Invoke-RegenerateStep {
  param(
      [Parameter(Mandatory)][string]$AppSlug,
      [Parameter(Mandatory)][string]$HeadRef,
      [Parameter(Mandatory)][string]$HeadSha,
      [Parameter(Mandatory)][string]$Repository,
      [Parameter(Mandatory)][string]$Solution
  )
  $userId = gh api "users/$AppSlug%5Bbot%5D" --jq .id
  Assert-ExitCode "Looking up the user id of $AppSlug[bot]"
  $identity = Get-BotIdentity -Slug $AppSlug -UserId ([string]$userId).Trim()

  $headAuthor = git log -1 --format=%an%x09%ae HEAD
  Assert-ExitCode 'git log'
  if (Test-AppCommit -NameAndEmail ([string]$headAuthor) -Identity $identity) {
    Write-StepSummary -Text "The head commit is already $($identity.Name)'s lock-file commit; nothing to do."
    return
  }

  $targets = Get-RestoreTarget -Solution $Solution -SolutionProjects (Get-SolutionProject -SlnxXml (Get-Content -Raw $Solution)) `
      -LockedProjects @(Get-LockedProject)
  foreach ($target in $targets) {
    Write-Host "::group::dotnet restore $target --force-evaluate"
    dotnet restore $target --force-evaluate | Out-Host
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { throw "dotnet restore $target failed (exit $code)" }
  }

  $status = @(git status --porcelain --untracked-files=all)
  Assert-ExitCode 'git status'
  $changed = Get-ChangedFile -Porcelain $status
  foreach ($path in $changed.Other) {
    Write-Host "::warning title=Dependabot lock files::Restore changed $path, which is not a lock file; it is not committed."
  }
  if ($changed.LockFiles.Count -eq 0) {
    Write-StepSummary -Text "Every lock file already matches the restore of $($targets.Count) target(s); nothing to commit."
    return
  }

  $message = Get-CommitMessage -Count $changed.LockFiles.Count -Identity $identity
  $commitSha = New-LockFileCommit -Repository $Repository -HeadSha $HeadSha -LockFiles @($changed.LockFiles) -Message $message

  # force=false: GitHub refuses the update unless the branch is still at HeadSha, the commit's parent.
  $updated = Invoke-GitHubApi -Method PATCH -Path "repos/$Repository/git/refs/heads/$HeadRef" `
      -Body ([ordered]@{ sha = $commitSha; force = $false }) -What "Updating $HeadRef" -AllowFailure
  if ($null -eq $updated) {
    $remoteHead = gh api "repos/$Repository/git/ref/heads/$HeadRef" --jq .object.sha
    $remoteHead = if ($LASTEXITCODE -eq 0) { ([string]$remoteHead).Trim() } else { '' }
    if ($remoteHead -and $remoteHead -ne $HeadSha) {
      Write-Host "::notice title=Dependabot lock files::$HeadRef moved to $remoteHead while this ran; the run for that commit regenerates the lock files."
      return
    }
    throw "Updating $HeadRef to $commitSha failed"
  }

  $list = ($changed.LockFiles | ForEach-Object { "- ``$_``" }) -join "`n"
  Write-StepSummary -Text "Committed $($changed.LockFiles.Count) regenerated lock file(s) to ${HeadRef} as $($identity.Name) ($commitSha, signed by GitHub):`n$list"
}

# Runs one step; returns the process exit code.
function Invoke-Step {
  param(
      [Parameter(Mandatory)][string]$Step,
      [string]$HasAppId, [string]$HasPrivateKey, [string]$AppSlug, [string]$HeadRef, [string]$HeadSha,
      [string]$Repository, [string]$Solution, [string]$Token
  )
  if ($Step -eq 'Credentials') {
    Invoke-CredentialStep -HasAppId $HasAppId -HasPrivateKey $HasPrivateKey
    return 0
  }
  try {
    if (-not $Token) { throw 'LOCKFILE_TOKEN is not set.' }
    Invoke-RegenerateStep -AppSlug $AppSlug -HeadRef $HeadRef -HeadSha $HeadSha -Repository $Repository -Solution $Solution
    return 0
  }
  catch {
    Write-Host "::error title=Dependabot lock files::$($_.Exception.Message)"
    return 1
  }
}

# Dot-sourcing for tests loads the functions without running a step.
if ($MyInvocation.InvocationName -eq '.') { return }

$exitCode = Invoke-Step -Step $Step -HasAppId $HasAppId -HasPrivateKey $HasPrivateKey -AppSlug $AppSlug -HeadRef $HeadRef `
    -HeadSha $HeadSha -Repository $Repository -Solution $Solution -Token $env:LOCKFILE_TOKEN
exit $exitCode
