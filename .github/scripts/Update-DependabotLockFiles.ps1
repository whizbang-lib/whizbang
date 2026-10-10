#!/usr/bin/env pwsh
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

    Two steps, so the workflow can stop before it needs a token or an SDK:

      Credentials  Decides whether the GitHub App's Dependabot secrets are set. Writes ready=true|false to
                   GITHUB_OUTPUT. Missing secrets are a notice, never a failure: the workflow ships before
                   the app exists.
      Regenerate   Restores, and when a lock file changed, commits it as the app's bot account with a DCO
                   sign-off and pushes to the pull request branch. Skips when the head commit is already
                   the app's (its own push re-triggers the workflow). The app token is read from the
                   LOCKFILE_TOKEN environment variable, never from an argument.

    Idempotent: a second restore over the regenerated files changes nothing, so a re-run commits nothing.
    A push rejected because the branch moved (Dependabot rebased it while this ran) is a notice: the run
    for the new head regenerates again.

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

function Get-CommitMessage {
  param([Parameter(Mandatory)][int]$Count)
  return @"
chore(deps): regenerate $Count NuGet lock file(s) for the Dependabot update

Dependabot updates Directory.Packages.props and the lock files of the projects that
reference a package directly. Projects that reach it through a project reference keep
the old version, and a locked-mode restore fails with NU1004. Regenerated with
dotnet restore --force-evaluate by .github/workflows/dependabot-lockfiles.yml.
"@
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
      [Parameter(Mandatory)][string]$Solution,
      [Parameter(Mandatory)][string]$Token
  )
  $userId = gh api "users/$AppSlug%5Bbot%5D" --jq .id
  Assert-ExitCode "Looking up the user id of $AppSlug[bot]"
  $identity = Get-BotIdentity -Slug $AppSlug -UserId ([string]$userId).Trim()

  $headAuthor = git log -1 --format=%ae HEAD
  Assert-ExitCode 'git log'
  if (([string]$headAuthor).Trim() -ieq $identity.Email) {
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

  git add -- @($changed.LockFiles) | Out-Host
  Assert-ExitCode 'git add'
  git -c "user.name=$($identity.Name)" -c "user.email=$($identity.Email)" commit --signoff --quiet -m (Get-CommitMessage -Count $changed.LockFiles.Count) | Out-Host
  Assert-ExitCode 'git commit'

  $remote = "https://x-access-token:$Token@github.com/$Repository.git"
  git push --quiet $remote "HEAD:refs/heads/$HeadRef" | Out-Host
  if ($LASTEXITCODE -ne 0) {
    $remoteHead = (@(git ls-remote $remote "refs/heads/$HeadRef") -join '') -split '\s+' | Select-Object -First 1
    if ($remoteHead -and $remoteHead -ne $HeadSha) {
      Write-Host "::notice title=Dependabot lock files::$HeadRef moved to $remoteHead while this ran; the run for that commit regenerates the lock files."
      return
    }
    throw "git push to $HeadRef failed"
  }

  $list = ($changed.LockFiles | ForEach-Object { "- ``$_``" }) -join "`n"
  Write-StepSummary -Text "Pushed $($changed.LockFiles.Count) regenerated lock file(s) to ${HeadRef} as $($identity.Name):`n$list"
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
    Invoke-RegenerateStep -AppSlug $AppSlug -HeadRef $HeadRef -HeadSha $HeadSha -Repository $Repository -Solution $Solution `
        -Token $Token
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
