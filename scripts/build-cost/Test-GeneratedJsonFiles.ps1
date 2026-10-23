<#
.SYNOPSIS
  Builds a consumer of the generators package and checks the message registry and pinned-type ledger it writes.

.DESCRIPTION
  The generators package writes .whizbang/pinned-type-ledger.json (a committed rename lockfile, on by default) and,
  when a project sets WhizbangEmitMessageRegistry=true, .whizbang/cache/message-registry.json (IDE tooling). Both
  are read from the built assembly, so they must work with EmitCompilerGeneratedFiles off, which is the default.

  The script writes a small consumer outside the repository, so no repository build file applies, importing the
  package's props and targets as a package reference would and referencing the generators as an analyzer, with
  EmitCompilerGeneratedFiles=false. It then checks, in order:
    1. a build with the package defaults writes the ledger and no registry, and no generated source on disk;
    2. a build with WhizbangEmitMessageRegistry=true writes the registry as well;
    3. after a new [PinnedId] message is added, a build adds it to both;
    4. once settled, a build with nothing changed compiles nothing and runs neither extraction target (#1665).

.PARAMETER OutputDirectory
  Where the consumer and the logs are written. Default: a new folder under the temp directory.

.PARAMETER Configuration
  Build configuration. Default: Debug.

.EXAMPLE
  pwsh scripts/build-cost/Test-GeneratedJsonFiles.ps1
#>
[CmdletBinding()]
param(
  [Parameter()]
  [string]$OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "generated-json-$([guid]::NewGuid().ToString('N'))"),

  [Parameter()]
  [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FirstPinnedId = '3f1c9a52-6d0e-4b7a-9e21-5c8d4a7b0f13'
$script:SecondPinnedId = '8a2e4d61-0c9b-4f35-a7d8-1e6b3c9f2a40'

<#
.SYNOPSIS
  The consumer's project file: the package's props and targets imported as a package reference imports them,
  the generators as an analyzer, and generated files kept off disk.
#>
function New-ConsumerProjectXml {
  [CmdletBinding()]
  [OutputType([string])]
  param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
  )

  $build = Join-Path $RepositoryRoot 'src/Whizbang.Generators/build'
  $core = Join-Path $RepositoryRoot 'src/Whizbang.Core/Whizbang.Core.csproj'
  $generators = Join-Path $RepositoryRoot 'src/Whizbang.Generators/Whizbang.Generators.csproj'
  return @"
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="$build/SoftwareExtravaganza.Whizbang.Generators.props" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EmitCompilerGeneratedFiles>false</EmitCompilerGeneratedFiles>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$core" />
    <ProjectReference Include="$generators" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <Import Project="$build/SoftwareExtravaganza.Whizbang.Generators.targets" />
</Project>
"@
}

<#
.SYNOPSIS
  A source file holding one [PinnedId] command.
#>
function New-PinnedCommandSource {
  [CmdletBinding()]
  [OutputType([string])]
  param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$PinnedId
  )

  return @"
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Consumer;

[PinnedId("$PinnedId")]
public sealed record $Name(Guid Id) : ICommand;
"@
}

if ($MyInvocation.InvocationName -eq '.') { return }

# Passing this script's own values: dot-sourcing runs that script's param block in this scope.
. (Join-Path $PSScriptRoot 'Test-NoOpRebuild.ps1') -OutputDirectory $OutputDirectory -Configuration $Configuration

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$consumer = Join-Path $OutputDirectory 'consumer'
New-Item -ItemType Directory -Force -Path $consumer | Out-Null
$project = Join-Path $consumer 'Consumer.csproj'
Set-Content -Path $project -Value (New-ConsumerProjectXml -RepositoryRoot $root)
Set-Content -Path (Join-Path $consumer 'FirstCommand.cs') -Value (New-PinnedCommandSource -Name 'FirstCommand' -PinnedId $FirstPinnedId)

$ledger = Join-Path $consumer '.whizbang/pinned-type-ledger.json'
$registry = Join-Path $consumer '.whizbang/cache/message-registry.json'
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-ConsumerBuild([string]$Label, [string[]]$Extra) {
  Write-Host "Building the consumer: $Label"
  dotnet build $project -c $Configuration -nologo -v:q @Extra | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "The consumer build failed ($Label, exit $LASTEXITCODE)." }
}

function Test-FileHas([string]$Path, [string]$Text, [string]$Step) {
  if (-not (Test-Path $Path)) {
    $failures.Add("${Step}: $Path was not written")
  } elseif (-not (Get-Content -Raw $Path).Contains($Text)) {
    $failures.Add("${Step}: $Path does not contain $Text")
  }
}

Invoke-ConsumerBuild 'package defaults' @()
Test-FileHas $ledger $FirstPinnedId '1. defaults'
if (Test-Path $registry) { $failures.Add("1. defaults: $registry was written with WhizbangEmitMessageRegistry off by default") }
if (Test-Path (Join-Path $consumer '.whizbang/cache/Whizbang.Generators')) { $failures.Add('1. defaults: generated source was written to disk') }

$on = @('-p:WhizbangEmitMessageRegistry=true')
Invoke-ConsumerBuild 'registry on' $on
Test-FileHas $registry 'Consumer.FirstCommand' '2. registry on'

Set-Content -Path (Join-Path $consumer 'SecondCommand.cs') -Value (New-PinnedCommandSource -Name 'SecondCommand' -PinnedId $SecondPinnedId)
Invoke-ConsumerBuild 'a new pinned message' $on
Test-FileHas $ledger $SecondPinnedId '3. new message'
Test-FileHas $registry 'Consumer.SecondCommand' '3. new message'

# The ledger is also an input to the generators (an AdditionalFile), so a build that changes it makes the next build
# compile once more; that build writes nothing new, and only the one after it has nothing to do.
Invoke-ConsumerBuild 'settle after the ledger changed' $on

$binlog = Join-Path $OutputDirectory 'noop.binlog'
$textLog = Join-Path $OutputDirectory 'noop.log'
Write-Host 'Building the consumer again with nothing changed'
dotnet build $project -c $Configuration -nologo -v:q --no-restore -m:1 @on "-bl:$binlog" "-flp:logfile=$textLog;verbosity=detailed" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "The no-op consumer build failed (exit $LASTEXITCODE)." }
$consumerProject = (Resolve-Path $project).Path
foreach ($w in @(Get-NoOpRebuildWork -LogPath $textLog -Targets @('CoreCompile', 'WhizbangExtractMessageRegistry', 'WhizbangExtractPinnedTypeLedger') |
    Where-Object Project -EQ $consumerProject)) {
  $failures.Add("4. no-op: $($w.Target) ran ($($w.Reason))")
}

if ($failures.Count -eq 0) {
  Write-Host 'The ledger and registry were written, updated, and skipped when nothing changed.'
  exit 0
}
Write-Host "Generated JSON checks failed ($($failures.Count)):"
foreach ($f in $failures) { Write-Host "  $f" }
Write-Host "Consumer and logs: $OutputDirectory"
exit 1
