#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when the pipeline uses a tool whose license nobody reviewed.

.DESCRIPTION
    Enumerates every tool the pipeline runs that it does not build itself:
      - every remote action and reusable workflow a workflow or composite action uses (owner/repo; local
        ./ actions and docker:// images excluded), GitHub's own included, since GitHub owning an action
        says nothing about its license;
      - every tool in the dotnet tool manifests (.config/dotnet-tools.json, dotnet-tools.json);
      - every "dotnet tool install" or "dotnet tool update" in a workflow or composite action;
      - every GitHub release asset a workflow or composite action downloads (owner/repo), such as the
        secret scanner and the OSV scanner binaries;
      - every container image a workflow or composite action runs (container:, services: image:,
        uses: docker://, docker run), by name without tag or digest.

    Each must be named in .github/pipeline-tool-licenses.txt with its SPDX license and the reason it is
    used. The list is the source of truth, so the check needs no network access; refresh it from the
    GitHub and NuGet APIs with Compare-PipelineToolLicenses.ps1.

    Allowed licenses: MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, PostgreSQL. Any other license passes
    only when its entry carries the "exception" marker and a reason, a change CODEOWNERS gives to
    build-admins. Strong copyleft (AGPL, GPL, SSPL, any version) never passes, with one precise exception: a
    container image run unmodified, only in CI, named in $CopyleftContainerExceptions in this script and marked
    'exception' in the list (today only fsfe/reuse). Adding another is a change to this script. The check also fails a list entry that nothing uses, an entry with no
    reason, an exception marker on a license that needs none, and a strong-copyleft license on the
    dependency review allow list (.github/dependency-review-config.yml).

    This exists because a release was canceled when dependency review, at the last step, refused the secret
    scanner action for its AGPL-3.0 license. Run by Test · Pipeline scripts through
    tests/Test-PipelineToolLicenses.Tests.ps1, so a new tool fails the pull request that adds it.

    Dot-source it for the functions; run it to check a repository (exit 0 clean, 1 with ::error:: lines).

.PARAMETER RepositoryRoot
    The repository to check. Defaults to the one this script is in.

.EXAMPLE
    pwsh .github/scripts/Test-PipelineToolLicenses.ps1
#>
[CmdletBinding()]
param(
  [Parameter()]
  [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-AllowedActions.ps1')
. (Join-Path $PSScriptRoot 'Test-PackageLicenses.ps1') -Directory $PSScriptRoot

# The licenses a pipeline tool may carry without a reviewed exception.
$script:AllowedToolLicenses = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'PostgreSQL')
$script:ToolKinds = @('action', 'dotnet-tool', 'release-binary', 'container')
# The only tools strong copyleft may pass for: container images run unmodified, only in CI, shipping nothing,
# and each still needs its list entry marked 'exception' with the reason. Adding one is a change to this script.
#   fsfe/reuse: the REUSE tool behind Gate · License headers (GPL-3.0-or-later), accepted on 2026-10-31 (#1938).
$script:CopyleftContainerExceptions = @('fsfe/reuse')

function Read-ToolLicenseList {
  <#
  .SYNOPSIS
      The entries of a pipeline tool license list: "<kind> <id> <SPDX license> [exception]  # reason", one
      per line, # starting a comment. Kind, Id (lowercase), License, Exception, Reason and Line.
  #>
  param([Parameter(Mandatory)][string]$Path)
  $number = 0
  foreach ($line in Get-Content -LiteralPath $Path) {
    $number++
    $hash = $line.IndexOf('#')
    $body = if ($hash -ge 0) { $line.Substring(0, $hash) } else { $line }
    $reason = if ($hash -ge 0) { $line.Substring($hash + 1).Trim() } else { '' }
    $fields = @($body.Trim() -split '\s+' | Where-Object { $_ })
    if ($fields.Count -eq 0) { continue }
    $valid = ($fields.Count -eq 3 -or ($fields.Count -eq 4 -and $fields[3] -eq 'exception')) -and ($script:ToolKinds -contains $fields[0])
    if (-not $valid) {
      throw "${Path}:${number}: expected '<$($script:ToolKinds -join '|')> <id> <SPDX license> [exception]  # reason', found '$line'"
    }
    [pscustomobject]@{
      Kind      = $fields[0]
      Id        = $fields[1].ToLowerInvariant()
      License   = $fields[2]
      Exception = ($fields.Count -eq 4)
      Reason    = $reason
      Line      = $number
    }
  }
}

function Get-ActionRepository {
  <# .SYNOPSIS The lowercase owner/repo of a uses: reference (owner/repo[/path]@ref), which holds its license. #>
  param([Parameter(Mandatory)][string]$Reference)
  $parts = ($Reference -replace '@.*$', '') -split '/'
  return "$($parts[0])/$($parts[1])".ToLowerInvariant()
}

function Get-DotnetToolInstall {
  <#
  .SYNOPSIS
      The lowercase package id a "dotnet tool install|update" command line installs, or nothing. An id that
      is an expression comes back as written, so it matches no entry and fails the check.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  if ($Text -match '^\s*#') { return }
  if ($Text -notmatch '\bdotnet\s+tool\s+(?:install|update)\s+(.*)$') { return }
  # The options that take a value; any other option is a switch.
  $withValue = @('--version', '--tool-path', '--add-source', '--configfile', '--framework', '-f', '--verbosity', '-v',
    '--tool-manifest', '-a', '--arch', '--source')
  $tokens = @(($Matches[1] -split '(&&|\|\||;|\||\s#)')[0] -split '\s+' | Where-Object { $_ -and $_ -ne '\' })
  for ($i = 0; $i -lt $tokens.Count; $i++) {
    $token = $tokens[$i]
    if ($token.StartsWith('-')) {
      if ($withValue -contains $token) { $i++ }
      continue
    }
    return $token.ToLowerInvariant()
  }
}

function Get-ReleaseDownload {
  <#
  .SYNOPSIS
      The lowercase owner/repo of every GitHub release asset a line downloads
      (https://github.com/<owner>/<repo>/releases/download/...), unless the line is a comment.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  if ($Text -match '^\s*#') { return }
  foreach ($match in [regex]::Matches($Text, 'https://github\.com/([^/\s]+)/([^/\s]+)/releases/download/')) {
    "$($match.Groups[1].Value)/$($match.Groups[2].Value)".ToLowerInvariant()
  }
}

function Get-ContainerImage {
  <#
  .SYNOPSIS
      The lowercase image name, without tag or digest, a line runs: "uses: docker://<image>", a job's
      "container: <image>", an "image: <image>" (a container or service block), or "docker run [options] <image>".
      An image that is an expression comes back as written, so it matches no entry and fails the check.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  if ($Text -match '^\s*#') { return }
  $image = $null
  if ($Text -match 'uses:\s*[''"]?docker://([^''"\s#]+)') {
    $image = $Matches[1]
  } elseif ($Text -match '^\s*(?:-\s*)?(?:container|image):\s*[''"]?([^''"\s#]+)') {
    $image = $Matches[1]
  } elseif ($Text -match '\bdocker\s+run\s+(.*)$') {
    # The options that take a separate value; any other option is a switch, and --name=value is one token.
    $withValue = @('-v', '--volume', '-e', '--env', '--env-file', '-w', '--workdir', '--name', '-p', '--publish', '--network',
      '--entrypoint', '-u', '--user', '--platform', '--mount', '-l', '--label', '--add-host', '--cpus', '-m', '--memory',
      '--pull', '--shm-size', '--ulimit', '--cap-add', '--cap-drop', '--tmpfs', '--hostname', '-h', '--device', '--gpus')
    $tokens = @(($Matches[1] -split '(&&|\|\||;|\||\s#)')[0] -split '\s+' | Where-Object { $_ -and $_ -ne '\' })
    for ($i = 0; $i -lt $tokens.Count; $i++) {
      if ($tokens[$i].StartsWith('-')) {
        if ($withValue -contains $tokens[$i]) { $i++ }
        continue
      }
      $image = $tokens[$i]
      break
    }
  }
  if (-not $image) { return }
  $image = $image -replace '@.*$', ''
  $slash = $image.LastIndexOf('/')
  $colon = $image.LastIndexOf(':')
  if ($colon -gt $slash) { $image = $image.Substring(0, $colon) }
  return $image.ToLowerInvariant()
}

function Get-PipelineTool {
  <# .SYNOPSIS Every third-party tool the pipeline runs: Kind (action or dotnet-tool), Id and Source (where). #>
  param([Parameter(Mandatory)][string]$RepositoryRoot)
  $github = Join-Path $RepositoryRoot '.github'

  foreach ($ref in Get-ActionReference -GitHubDirectory $github) {
    [pscustomobject]@{ Kind = 'action'; Id = (Get-ActionRepository -Reference $ref.Reference); Source = "$($ref.File):$($ref.Line)" }
  }

  foreach ($manifest in @((Join-Path $RepositoryRoot '.config' 'dotnet-tools.json'), (Join-Path $RepositoryRoot 'dotnet-tools.json'))) {
    if (-not (Test-Path -LiteralPath $manifest)) { continue }
    $json = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if (-not $json.PSObject.Properties['tools']) { continue }
    foreach ($name in $json.tools.PSObject.Properties.Name) {
      [pscustomobject]@{ Kind = 'dotnet-tool'; Id = $name.ToLowerInvariant(); Source = $manifest }
    }
  }

  $files = @(
    Get-ChildItem -Path (Join-Path $github 'workflows') -Include '*.yml', '*.yaml' -Recurse -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $github 'actions') -Include 'action.yml', 'action.yaml' -Recurse -File -ErrorAction SilentlyContinue
  )
  foreach ($file in $files) {
    $number = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
      $number++
      $id = Get-DotnetToolInstall -Text $line
      if ($id) { [pscustomobject]@{ Kind = 'dotnet-tool'; Id = $id; Source = "$($file.FullName):$number" } }
      foreach ($repo in @(Get-ReleaseDownload -Text $line)) {
        [pscustomobject]@{ Kind = 'release-binary'; Id = $repo; Source = "$($file.FullName):$number" }
      }
      $image = Get-ContainerImage -Text $line
      if ($image) { [pscustomobject]@{ Kind = 'container'; Id = $image; Source = "$($file.FullName):$number" } }
    }
  }
}

function Test-StrongCopyleft {
  <# .SYNOPSIS Whether any license in an SPDX expression is AGPL, GPL or SSPL (any version); LGPL is not. #>
  param([Parameter(Mandatory)][string]$License)
  foreach ($id in @($License -split '[\s()]+' | Where-Object { $_ })) {
    if ($id -match '^(AGPL|GPL|SSPL)-') { return $true }
  }
  return $false
}

function Get-ToolLicenseViolation {
  <# .SYNOPSIS One message per problem with the tools in use against the list; nothing when it is clean. #>
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Tools,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$List
  )
  foreach ($group in @($List | Group-Object Kind, Id | Where-Object Count -GT 1)) {
    "$($group.Group[0].Kind) $($group.Group[0].Id) is listed more than once (lines $(@($group.Group.Line) -join ', '))."
  }

  foreach ($entry in $List) {
    $name = "$($entry.Kind) $($entry.Id) ($($entry.License), line $($entry.Line))"
    if (-not $entry.Reason) { "$name has no reason; say why the pipeline uses it after a #." }
    if (Test-StrongCopyleft -License $entry.License) {
      $reviewed = $entry.Kind -eq 'container' -and $entry.Exception -and ($script:CopyleftContainerExceptions -contains $entry.Id)
      if (-not $reviewed) {
        "$name is strong copyleft, which no pipeline tool may carry; the only exceptions are the CI-only containers run unmodified in Test-PipelineToolLicenses.ps1 ($($script:CopyleftContainerExceptions -join ', ')), marked 'exception'. Replace the tool."
      }
    } elseif ($script:AllowedToolLicenses -notcontains $entry.License -and -not $entry.Exception) {
      "$name is not one of $($script:AllowedToolLicenses -join ', '); replace it, or mark it 'exception' with the reason, for a build admin to review."
    } elseif ($script:AllowedToolLicenses -contains $entry.License -and $entry.Exception) {
      "$name is marked 'exception', which is not needed for an allowed license; remove the marker."
    }
    if (-not ($Tools | Where-Object { $_.Kind -eq $entry.Kind -and $_.Id -eq $entry.Id })) {
      "$name is listed but nothing uses it; remove the entry."
    }
  }

  foreach ($group in @($Tools | Group-Object Kind, Id)) {
    $tool = $group.Group[0]
    if ($List | Where-Object { $_.Kind -eq $tool.Kind -and $_.Id -eq $tool.Id }) { continue }
    "$($tool.Kind) $($tool.Id) is not in .github/pipeline-tool-licenses.txt; used at $(@($group.Group.Source) -join ', '). Add it with its SPDX license and why it is used."
  }
}

function Get-ReviewConfigCopyleft {
  <# .SYNOPSIS The strong-copyleft licenses on a dependency review configuration's allow-licenses list. #>
  param([Parameter(Mandatory)][string]$Path)
  @(Read-YamlList -Lines @(Get-Content -LiteralPath $Path) -Key 'allow-licenses') | Where-Object { Test-StrongCopyleft -License $_ }
}

# Dot-sourcing for tests loads the functions without checking anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$list = @(Read-ToolLicenseList -Path (Join-Path $root '.github' 'pipeline-tool-licenses.txt'))
$tools = @(Get-PipelineTool -RepositoryRoot $root)
$problems = @(Get-ToolLicenseViolation -Tools $tools -List $list)
$reviewConfig = Join-Path $root '.github' 'dependency-review-config.yml'
if (Test-Path -LiteralPath $reviewConfig) {
  $problems += @(Get-ReviewConfigCopyleft -Path $reviewConfig | ForEach-Object { "$_ is strong copyleft and is on allow-licenses in .github/dependency-review-config.yml; remove it." })
}
if ($problems.Count -eq 0) {
  $kinds = $tools | Group-Object Kind, Id | Group-Object { $_.Group[0].Kind } | ForEach-Object { "$($_.Name): $($_.Count)" }
  Write-Output "Every pipeline tool is under a reviewed license ($($kinds -join ', '))."
  exit 0
}
foreach ($problem in $problems) { Write-Output "::error::$problem" }
exit 1
