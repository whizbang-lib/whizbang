#Requires -Version 7.0

<#
.SYNOPSIS
    The Docker Hub image mirror CI pulls from: reading .github/ci-images.txt, naming an image's mirror
    copy, and finding pulled Docker Hub images the manifest does not list.

.DESCRIPTION
    CI sets TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX to ghcr.io/whizbang-lib/ci-mirror/ once the mirror holds
    every manifest image (Select-CiImageSource.ps1). Testcontainers reads that variable itself and
    prefixes every Docker Hub image it pulls, the reaper included. Images started with a plain
    `docker run` (Run-Tests.ps1's shared containers) go through Resolve-CiImage, which applies the same
    rule, so one variable moves every pull. Locally the variable is unset and nothing changes.

    The prefix rule is Testcontainers' own (DockerImage.ApplyHubImageNamePrefix in 4.x): an image whose
    first path segment is a registry host (it has a dot or a colon, or is "localhost") is left alone;
    any other image is a Docker Hub image and becomes <prefix>/<image>. The C# twin is
    Whizbang.Testing.Containers.CiImages.
#>

Set-StrictMode -Version Latest

# The variable Testcontainers reads, and the one every other pull follows.
$script:PrefixVariable = 'TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX'

# Host names that mean Docker Hub when a pulled image's name carries one.
$script:DockerHubHosts = @('docker.io', 'index.docker.io', 'registry-1.docker.io')

# <name>:<tag>@sha256:<64 hex>, the only shape a manifest line may take.
$script:ManifestLine = '^(?<name>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*):(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})@(?<digest>sha256:[a-f0-9]{64})$'

function Get-CiImagePrefixVariable {
  <# .SYNOPSIS The name of the environment variable that holds the mirror prefix. #>
  [CmdletBinding()]
  [OutputType([string])]
  param()
  $script:PrefixVariable
}

function Test-CiImageHasRegistry {
  <#
  .SYNOPSIS
      Whether an image name starts with a registry host, by Docker's rule: the first path segment
      contains a dot or a colon, or is "localhost". "rabbitmq" and "pgvector/pgvector" do not.
  #>
  [CmdletBinding()]
  [OutputType([bool])]
  param([Parameter(Mandatory)][string]$Image)
  $slash = $Image.IndexOf('/')
  if ($slash -lt 0) { return $false }
  $first = $Image.Substring(0, $slash)
  return $first.Contains('.') -or $first.Contains(':') -or $first -eq 'localhost'
}

function Resolve-CiImage {
  <#
  .SYNOPSIS
      The name to pull a Docker Hub image by: its mirror copy when a prefix is set, itself otherwise.
  .PARAMETER Image
      The image as the code names it, e.g. pgvector/pgvector:pg17.
  .PARAMETER Prefix
      The mirror prefix. Defaults to TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX; empty means Docker Hub.
  #>
  [CmdletBinding()]
  [OutputType([string])]
  param(
    [Parameter(Mandatory)][string]$Image,
    [AllowEmptyString()][AllowNull()][string]$Prefix = [Environment]::GetEnvironmentVariable($script:PrefixVariable)
  )
  if ([string]::IsNullOrWhiteSpace($Prefix) -or (Test-CiImageHasRegistry -Image $Image)) { return $Image }
  return "$($Prefix.Trim().Trim('/'))/$Image"
}

function Read-CiImageManifest {
  <#
  .SYNOPSIS
      The images .github/ci-images.txt lists, one object per line: Name, Tag, Digest, Image (name:tag)
      and Source (the Docker Hub reference the mirror copies, pinned by digest).
  .DESCRIPTION
      Blank lines and lines starting with # are skipped. Any other line must be name:tag@sha256:digest;
      a line that is not throws, naming the line, so a typo cannot silently drop an image.
  #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][string]$Path)
  $number = 0
  $images = foreach ($line in Get-Content -LiteralPath $Path) {
    $number++
    $text = $line.Trim()
    if ($text -eq '' -or $text.StartsWith('#')) { continue }
    if ($text -notmatch $script:ManifestLine) {
      throw "${Path}:${number}: '$text' is not <docker hub name>:<tag>@sha256:<digest>"
    }
    if (Test-CiImageHasRegistry -Image $Matches.name) {
      throw "${Path}:${number}: '$text' names a registry; list Docker Hub images only, by the name the code uses"
    }
    [pscustomobject]@{
      Name   = $Matches.name
      Tag    = $Matches.tag
      Digest = $Matches.digest
      Image  = "$($Matches.name):$($Matches.tag)"
      Source = "docker.io/$($Matches.name):$($Matches.tag)@$($Matches.digest)"
    }
  }
  $duplicate = @($images | Group-Object Image | Where-Object Count -gt 1 | Select-Object -First 1)
  if ($duplicate.Count -gt 0) { throw "${Path}: '$($duplicate[0].Name)' is listed more than once" }
  return @($images)
}

function ConvertTo-CiImageName {
  <#
  .SYNOPSIS
      Reduces a pulled image (as `docker image ls` prints Repository:Tag) to the Docker Hub name the
      manifest uses, or $null when it is not a Docker Hub image (another registry, or untagged).
  .DESCRIPTION
      A mirror copy (<prefix>/<name>:<tag>) reduces to <name>:<tag>; docker.io/library/rabbitmq:3 and
      rabbitmq:3 both reduce to rabbitmq:3.
  #>
  [CmdletBinding()]
  [OutputType([string])]
  param(
    [Parameter(Mandatory)][string]$Reference,
    [AllowEmptyString()][AllowNull()][string]$Prefix
  )
  $at = $Reference.IndexOf('@')
  if ($at -ge 0) { $Reference = $Reference.Substring(0, $at) }
  $colon = $Reference.LastIndexOf(':')
  if ($colon -le $Reference.LastIndexOf('/')) { return $null }  # no tag: nothing to match a line by
  $repository = $Reference.Substring(0, $colon)
  $tag = $Reference.Substring($colon + 1)
  if ($repository -eq '<none>' -or $tag -eq '<none>') { return $null }

  $mirror = if ([string]::IsNullOrWhiteSpace($Prefix)) { $null } else { "$($Prefix.Trim().Trim('/'))/" }
  if ($mirror -and $repository.StartsWith($mirror, [StringComparison]::Ordinal)) {
    $repository = $repository.Substring($mirror.Length)
  } elseif (Test-CiImageHasRegistry -Image $repository) {
    $slash = $repository.IndexOf('/')
    if ($script:DockerHubHosts -notcontains $repository.Substring(0, $slash)) { return $null }
    $repository = $repository.Substring($slash + 1)
  }
  if ($repository.StartsWith('library/', [StringComparison]::Ordinal)) { $repository = $repository.Substring(8) }
  return "${repository}:${tag}"
}

function Find-UnlistedCiImage {
  <#
  .SYNOPSIS
      The Docker Hub images among Pulled (Repository:Tag strings) that the manifest does not list.
  #>
  [CmdletBinding()]
  [OutputType([string[]])]
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Pulled,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Manifest,
    [AllowEmptyString()][AllowNull()][string]$Prefix
  )
  $listed = @($Manifest | ForEach-Object Image)
  $unlisted = foreach ($reference in $Pulled) {
    $name = ConvertTo-CiImageName -Reference $reference -Prefix $Prefix
    if ($null -ne $name -and $listed -notcontains $name) { $name }
  }
  return @($unlisted | Sort-Object -Unique)
}

Export-ModuleMember -Function Get-CiImagePrefixVariable, Test-CiImageHasRegistry, Resolve-CiImage,
  Read-CiImageManifest, ConvertTo-CiImageName, Find-UnlistedCiImage
