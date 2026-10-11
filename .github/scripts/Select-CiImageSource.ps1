#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Points a test job's image pulls at the GHCR mirror when it holds every manifest image, and records
    which images the runner already had, for Test-CiImagePulls.ps1.

.DESCRIPTION
    For each image in .github/ci-images.txt, reads the digest its mirror copy carries. When every copy
    is there at the manifest's digest, appends TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=<prefix> to
    $GITHUB_ENV, so the job's later steps pull from the mirror: Testcontainers reads the variable
    itself, and Run-Tests.ps1 and the C# fixtures resolve their `docker run` images through it.

    When any copy is missing or stale (the mirror has not run since the manifest changed, or the
    registry could not be read), the variable is left unset and the job pulls from Docker Hub as it
    did before the mirror, with a warning naming what is missing. The mirror never makes a job fail
    that would have passed without it, and a pull request that adds an image is not blocked waiting
    for the mirror run that its own merge triggers.

    Always exits 0.

.PARAMETER ManifestPath
    The image manifest. Default: .github/ci-images.txt under the repository root.

.PARAMETER Prefix
    The mirror prefix. Default: ghcr.io/whizbang-lib/ci-mirror/.

.PARAMETER BaselinePath
    Where to write the images the runner holds before the tests (one Repository:Tag per line).
    Default: $RUNNER_TEMP/ci-images-baseline.txt.

.EXAMPLE
    pwsh .github/scripts/Select-CiImageSource.ps1
#>

param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..' 'ci-images.txt'),
    [string]$Prefix = 'ghcr.io/whizbang-lib/ci-mirror/',
    [string]$BaselinePath = (Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'ci-images-baseline.txt'),
    [string]$EnvFile = $env:GITHUB_ENV
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..' '..' 'scripts' 'lib' 'CiImages.psm1') -Force

# The digest a registry reference carries, or $null when it cannot be read (absent, or no access).
function Get-RemoteImageDigest {
  param([Parameter(Mandatory)][string]$Reference)
  $digest = docker buildx imagetools inspect $Reference --format '{{.Manifest.Digest}}' 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  return "$digest".Trim()
}

# Repository:Tag of every image the local daemon holds.
function Get-LocalImage {
  return @(docker image ls --format '{{.Repository}}:{{.Tag}}' 2>$null)
}

function Select-CiImageSource {
  param(
    [Parameter(Mandatory)][object[]]$Manifest,
    [Parameter(Mandatory)][string]$Prefix,
    [Parameter(Mandatory)][scriptblock]$GetDigest
  )
  $missing = foreach ($image in $Manifest) {
    $mirror = Resolve-CiImage -Image $image.Image -Prefix $Prefix
    $digest = & $GetDigest $mirror
    if ($digest -ne $image.Digest) { "$mirror (has $(if ($digest) { $digest } else { 'nothing readable' }), wants $($image.Digest))" }
  }
  [pscustomobject]@{ UseMirror = @($missing).Count -eq 0; Missing = @($missing) }
}

function Invoke-SelectCiImageSource {
  param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$Prefix,
    [Parameter(Mandatory)][string]$BaselinePath,
    [AllowEmptyString()][AllowNull()][string]$EnvFile,
    [scriptblock]$GetDigest = { param($r) Get-RemoteImageDigest -Reference $r },
    [scriptblock]$ListImages = { Get-LocalImage }
  )
  Set-Content -LiteralPath $BaselinePath -Value @(& $ListImages)

  $choice = Select-CiImageSource -Manifest (Read-CiImageManifest -Path $ManifestPath) -Prefix $Prefix -GetDigest $GetDigest
  if (-not $choice.UseMirror) {
    $list = $choice.Missing -join '; '
    Write-Output "::warning title=CI image mirror incomplete::Pulling from Docker Hub this run. Not mirrored at the manifest digest: $list. Run the 'Mirror CI images' workflow."
    return
  }
  $line = "$(Get-CiImagePrefixVariable)=$Prefix"
  if ($EnvFile) { Add-Content -LiteralPath $EnvFile -Value $line }
  Write-Output "Pulling Docker Hub images from the mirror: $line"
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-SelectCiImageSource -ManifestPath $ManifestPath -Prefix $Prefix -BaselinePath $BaselinePath -EnvFile $EnvFile
exit 0
