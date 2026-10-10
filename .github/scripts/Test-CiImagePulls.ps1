#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fails a test job that pulled a Docker Hub image .github/ci-images.txt does not list.

.DESCRIPTION
    Runs after the tests. Lists the images the runner holds, drops the ones it held before the tests
    (Select-CiImageSource.ps1 wrote that baseline), and checks every remaining Docker Hub image
    against the manifest: a mirror copy counts as the Docker Hub image it copies, and images from any
    other registry (mcr.microsoft.com, ...) are ignored, since they are not rate limited.

    This is the drift guard. It sees what was actually pulled, so it catches every way an image
    arrives: a string literal, a Testcontainers builder's default image, the reaper whose tag moves
    with the Testcontainers package, a `docker run` in a script. A new unlisted image would pull from
    Docker Hub unmirrored (or, with the mirror active, fail outright on a name the mirror lacks); here
    it fails by name, with the fix.

    Exit 0 when every pulled Docker Hub image is listed, 1 otherwise.

.PARAMETER ManifestPath
    The image manifest. Default: .github/ci-images.txt under the repository root.

.PARAMETER Prefix
    The mirror prefix in effect. Default: TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX (empty without the mirror).

.PARAMETER BaselinePath
    The images held before the tests. Default: $RUNNER_TEMP/ci-images-baseline.txt. A missing file
    counts as an empty baseline.

.EXAMPLE
    pwsh .github/scripts/Test-CiImagePulls.ps1
#>

param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..' 'ci-images.txt'),
    [string]$Prefix = $env:TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX,
    [string]$BaselinePath = (Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'ci-images-baseline.txt')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..' '..' 'scripts' 'lib' 'CiImages.psm1') -Force

function Get-LocalImage {
  return @(docker image ls --format '{{.Repository}}:{{.Tag}}' 2>$null)
}

function Invoke-TestCiImagePulls {
  [OutputType([int])]
  param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [AllowEmptyString()][AllowNull()][string]$Prefix,
    [Parameter(Mandatory)][string]$BaselinePath,
    [scriptblock]$ListImages = { Get-LocalImage }
  )
  $manifest = Read-CiImageManifest -Path $ManifestPath
  $before = if (Test-Path -LiteralPath $BaselinePath) { @(Get-Content -LiteralPath $BaselinePath) } else { @() }
  $pulled = @(& $ListImages | Where-Object { $_ -and $before -notcontains $_ })

  $unlisted = @(Find-UnlistedCiImage -Pulled $pulled -Manifest $manifest -Prefix $Prefix)
  if ($unlisted.Count -eq 0) {
    Write-Output "Every Docker Hub image this job pulled is in the CI image manifest ($($pulled.Count) image(s) pulled)."
    return 0
  }
  foreach ($image in $unlisted) {
    Write-Output "::error title=Docker Hub image not in the CI image manifest::$image was pulled from Docker Hub but .github/ci-images.txt does not list it, so CI is exposed to Docker Hub's pull rate limit. Add it with its digest (ai-docs/ci-image-mirror.md)."
  }
  return 1
}

if ($MyInvocation.InvocationName -eq '.') { return }

# The function writes its report, then the exit code as its last output.
$output = @(Invoke-TestCiImagePulls -ManifestPath $ManifestPath -Prefix $Prefix -BaselinePath $BaselinePath)
$output | Select-Object -SkipLast 1
exit $output[-1]
