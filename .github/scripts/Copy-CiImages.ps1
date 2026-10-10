#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Copies every image in .github/ci-images.txt from Docker Hub to the GHCR mirror, by digest.

.DESCRIPTION
    For each manifest image, compares the digest its mirror copy carries with the manifest's. Equal:
    nothing to do. Otherwise copies <name>:<tag>@<digest> from Docker Hub to <prefix>/<name>:<tag> with
    `docker buildx imagetools create`, which copies the whole multi-arch index, so the copy keeps the
    source's digest and every platform. The copy is then read back and must carry the manifest digest.

    A copy that fails (Docker Hub's rate limit is the usual cause) is retried after a pause. Exit 0
    when every mirror copy matches the manifest, 1 otherwise.

    Needs `docker login ghcr.io` with packages: write. Run by .github/workflows/mirror-ci-images.yml.

.PARAMETER ManifestPath
    The image manifest. Default: .github/ci-images.txt under the repository root.

.PARAMETER Prefix
    The mirror prefix. Default: ghcr.io/whizbang-lib/ci-mirror/.

.PARAMETER Attempts
    Copy attempts per image. Default: 3.

.PARAMETER RetryDelaySeconds
    Pause between attempts, doubled each time. Default: 30.

.EXAMPLE
    pwsh .github/scripts/Copy-CiImages.ps1
#>

param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..' 'ci-images.txt'),
    [string]$Prefix = 'ghcr.io/whizbang-lib/ci-mirror/',
    [ValidateRange(1, 10)][int]$Attempts = 3,
    [ValidateRange(0, 600)][int]$RetryDelaySeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..' '..' 'scripts' 'lib' 'CiImages.psm1') -Force

function Get-RemoteImageDigest {
  param([Parameter(Mandatory)][string]$Reference)
  $digest = docker buildx imagetools inspect $Reference --format '{{.Manifest.Digest}}' 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  return "$digest".Trim()
}

# Copies source to target; returns whether docker reported success.
function Copy-RemoteImage {
  param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Target)
  docker buildx imagetools create --tag $Target $Source | Out-Host
  return $LASTEXITCODE -eq 0
}

function Invoke-CopyCiImages {
  [OutputType([int])]
  param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$Prefix,
    [int]$Attempts = 3,
    [int]$RetryDelaySeconds = 30,
    [scriptblock]$GetDigest = { param($r) Get-RemoteImageDigest -Reference $r },
    [scriptblock]$Copy = { param($s, $t) Copy-RemoteImage -Source $s -Target $t }
  )
  $failed = 0
  foreach ($image in (Read-CiImageManifest -Path $ManifestPath)) {
    $target = Resolve-CiImage -Image $image.Image -Prefix $Prefix
    if ((& $GetDigest $target) -eq $image.Digest) {
      Write-Output "up to date: $target ($($image.Digest))"
      continue
    }
    $delay = $RetryDelaySeconds
    $copied = $false
    for ($attempt = 1; $attempt -le $Attempts -and -not $copied; $attempt++) {
      Write-Output "copying ($attempt/$Attempts): $($image.Source) -> $target"
      $copied = (& $Copy $image.Source $target) -and ((& $GetDigest $target) -eq $image.Digest)
      if (-not $copied -and $attempt -lt $Attempts) {
        Write-Output "  not copied yet; retrying in ${delay}s"
        Start-Sleep -Seconds $delay
        $delay *= 2
      }
    }
    if ($copied) {
      Write-Output "copied: $target ($($image.Digest))"
    } else {
      Write-Output "::error title=CI image not mirrored::$($image.Source) could not be copied to $target at its digest after $Attempts attempt(s)."
      $failed++
    }
  }
  return [int]($failed -gt 0)
}

if ($MyInvocation.InvocationName -eq '.') { return }

# The function writes its report, then the exit code as its last output.
$output = @(Invoke-CopyCiImages -ManifestPath $ManifestPath -Prefix $Prefix -Attempts $Attempts -RetryDelaySeconds $RetryDelaySeconds)
$output | Select-Object -SkipLast 1
exit $output[-1]
