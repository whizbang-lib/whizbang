#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Publishes the local commits the remote does not have through the GitHub API, so GitHub signs them,
    then creates or moves the branch. Use it in a workflow in place of git push.

.DESCRIPTION
    The develop, main and release/** rulesets require signed commits, and a commit a workflow makes with
    git commit and pushes with git push is unsigned. GitHub signs a commit a GitHub App (or the workflow's
    own token) creates through the REST commits API when the request sets no author, committer or
    signature ("Signature verification for bots"), and that API takes several parents, so merges work too.

    For each local commit not on any remote-tracking branch, oldest first, this:
      1. builds its tree through the API: the parent's tree plus the changed paths (text inline, anything
         else as a blob, deletions as a null sha), and checks the result is the tree git built, so the
         published commit has exactly the local content;
      2. creates the commit with the same message and parents (a parent published in this run maps to its
         new sha), and no author or committer, so GitHub signs it;
    then points the branch at the last one (created if new; moved, forced only with -Force) and moves the
    local branch onto the published commits, so later steps see what the remote has.

    Run it from the repository's working tree, with GH_TOKEN set to the token that should author the
    commits (the release app's, so the pull request it opens triggers CI).

.PARAMETER Repository
    owner/name.

.PARAMETER Branch
    The branch to create or move.

.PARAMETER Force
    Move the branch even when the result is not a fast-forward (as git push --force).

.PARAMETER Remote
    The remote whose tracking branches count as already published.

.OUTPUTS
    The sha of the published head, or nothing when there was nothing to publish.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [Parameter(Mandatory)][string]$Branch,
  [switch]$Force,
  [string]$Remote = 'origin'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-DiffTree {
  <# .SYNOPSIS The changes in git diff-tree --name-status -z output: Status (A, M, D, T) and Path. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Raw)
  $parts = @($Raw -split "`0" | Where-Object { $_ -ne '' })
  for ($i = 0; $i + 1 -lt $parts.Count; $i += 2) {
    [pscustomobject]@{ Status = $parts[$i].Substring(0, 1); Path = $parts[$i + 1] }
  }
}

function Test-IsText {
  <# .SYNOPSIS Whether bytes can go in a tree entry's content: valid UTF-8 with no NUL. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
  if ([Array]::IndexOf($Bytes, [byte]0) -ge 0) { return $false }
  try {
    [void]([Text.UTF8Encoding]::new($false, $true)).GetString($Bytes)
    return $true
  } catch [System.Text.DecoderFallbackException] {
    return $false
  }
}

function Invoke-GitHubApi {
  <# .SYNOPSIS One REST call with a JSON body; null for a 404 when AllowNotFound. #>
  param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, [hashtable]$Body, [switch]$AllowNotFound)
  $arguments = @('api', '--method', $Method, $Path)
  $out = if ($Body) { ConvertTo-Json -InputObject $Body -Depth 20 -Compress | & gh @arguments --input - 2>&1 } else { & gh @arguments 2>&1 }
  if ($LASTEXITCODE -ne 0) {
    if ($AllowNotFound -and ($out -join "`n") -match 'HTTP 404') { return $null }
    throw "gh api $Method $Path failed (exit $LASTEXITCODE): $($out -join ' ')"
  }
  return ($out -join "`n") | ConvertFrom-Json
}

function Invoke-GitText {
  <# .SYNOPSIS Runs git and returns its output as one string; throws when git fails. #>
  $out = & git @args
  if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
  return ($out -join "`n")
}

function Get-BlobBytes {
  <# .SYNOPSIS The bytes of a file as committed, without any line-ending conversion. #>
  param([Parameter(Mandatory)][string]$Commit, [Parameter(Mandatory)][string]$Path)
  $psi = [Diagnostics.ProcessStartInfo]::new('git')
  foreach ($a in @('cat-file', 'blob', "${Commit}:$Path")) { $psi.ArgumentList.Add($a) }
  # The process's own directory is not PowerShell's location; read from the repository being published.
  $psi.WorkingDirectory = (Get-Location).ProviderPath
  $psi.RedirectStandardOutput = $true
  $psi.UseShellExecute = $false
  $p = [Diagnostics.Process]::Start($psi)
  $ms = [IO.MemoryStream]::new()
  $p.StandardOutput.BaseStream.CopyTo($ms)
  $p.WaitForExit()
  if ($p.ExitCode -ne 0) { throw "git cat-file blob ${Commit}:$Path failed (exit $($p.ExitCode))" }
  return , $ms.ToArray()
}

function New-TreeEntry {
  <# .SYNOPSIS The API tree entry for one changed path of a commit. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Commit, [Parameter(Mandatory)]$Change)
  if ($Change.Status -eq 'D') {
    return @{ path = $Change.Path; mode = '100644'; type = 'blob'; sha = $null }
  }
  # "<mode> <type> <sha>\t<path>"
  $line = Invoke-GitText ls-tree $Commit -- $Change.Path
  $mode, $type, $sha = ($line -split "`t", 2)[0] -split ' '
  if ($type -eq 'commit') { return @{ path = $Change.Path; mode = $mode; type = 'commit'; sha = $sha } }
  $bytes = Get-BlobBytes -Commit $Commit -Path $Change.Path
  if ($mode -ne '120000' -and (Test-IsText -Bytes $bytes)) {
    return @{ path = $Change.Path; mode = $mode; type = 'blob'; content = [Text.Encoding]::UTF8.GetString($bytes) }
  }
  $blob = Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/blobs" -Body @{ content = [Convert]::ToBase64String($bytes); encoding = 'base64' }
  return @{ path = $Change.Path; mode = $mode; type = 'blob'; sha = $blob.sha }
}

function Sync-LocalBranch {
  <# .SYNOPSIS Moves the local branch onto the published commits, which have the same trees. #>
  param([Parameter(Mandatory)][string]$Branch, [Parameter(Mandatory)][string]$Remote)
  [void](Invoke-GitText fetch -q $Remote "+refs/heads/${Branch}:refs/remotes/$Remote/$Branch")
  [void](Invoke-GitText reset -q --soft "$Remote/$Branch")
}

function Publish-SignedCommits {
  <# .SYNOPSIS Recreates the unpublished commits through the API and points the branch at them. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Branch, [switch]$Force, [string]$Remote = 'origin')
  $pending = @((Invoke-GitText rev-list --reverse --topo-order HEAD --not "--remotes=$Remote") -split "`n" | Where-Object { $_ })
  $published = @{}
  # With nothing new, the branch still has to point at HEAD, which the remote already has.
  $head = (Invoke-GitText rev-parse HEAD).Trim()
  foreach ($commit in $pending) {
    $parents = @(((Invoke-GitText rev-list --parents -n 1 $commit) -split ' ') | Select-Object -Skip 1)
    $first = $parents[0]
    $changes = @(ConvertFrom-DiffTree -Raw (Invoke-GitText diff-tree -r -z --no-renames --no-commit-id --name-status $first $commit))
    $expected = (Invoke-GitText rev-parse "$commit^{tree}").Trim()
    $tree = if ($changes.Count -eq 0) {
      (Invoke-GitText rev-parse "$first^{tree}").Trim()
    } else {
      $entries = @($changes | ForEach-Object { New-TreeEntry -Repository $Repository -Commit $commit -Change $_ })
      (Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/trees" -Body @{ base_tree = (Invoke-GitText rev-parse "$first^{tree}").Trim(); tree = $entries }).sha
    }
    if ($tree -ne $expected) {
      throw "The API built tree $tree for commit $($commit.Substring(0, 9)), not git's $expected; nothing was published."
    }

    $message = Invoke-GitText log -1 --format=%B $commit
    $remoteParents = @($parents | ForEach-Object { if ($published.ContainsKey($_)) { $published[$_] } else { $_ } })
    # No author, committer or signature: GitHub then signs the commit as the token's app.
    $created = Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/commits" -Body @{ message = $message.TrimEnd("`n"); tree = $tree; parents = $remoteParents }
    if (-not ($created.PSObject.Properties['verification'] -and $created.verification.verified)) {
      Write-Warning "Commit $($created.sha) was created but GitHub did not report it as verified."
    }
    $published[$commit] = $created.sha
    $head = $created.sha
  }

  $existing = Invoke-GitHubApi -Method GET -Path "repos/$Repository/git/ref/heads/$Branch" -AllowNotFound
  if ($existing -and $existing.object.sha -eq $head) {
    Write-Host "Nothing to publish: $Branch already points at $head."
    return
  }
  if ($existing) {
    [void](Invoke-GitHubApi -Method PATCH -Path "repos/$Repository/git/refs/heads/$Branch" -Body @{ sha = $head; force = [bool]$Force })
  } else {
    [void](Invoke-GitHubApi -Method POST -Path "repos/$Repository/git/refs" -Body @{ ref = "refs/heads/$Branch"; sha = $head })
  }
  Sync-LocalBranch -Branch $Branch -Remote $Remote
  Write-Host "Published $($pending.Count) commit(s) to $Branch at $head."
  return $head
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

Publish-SignedCommits -Repository $Repository -Branch $Branch -Force:$Force -Remote $Remote | Out-Null
exit 0
