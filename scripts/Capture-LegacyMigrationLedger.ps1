#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Captures the migration ledger hashes a published release recorded, from that release's own packages.

.DESCRIPTION
    Releases before #1681 recorded each migration's ledger hash as SHA-256 of the text the driver ran, and that text
    differs between releases in ways the current code cannot reproduce from today's files (the license header added
    in aadb4cc45, the EF Core driver's earlier schema qualifier). The upgrade tests (#2125) therefore assert against
    hashes captured from the published packages of a real release, never derived from today's text.

    The script builds a throwaway console app in a temporary directory, outside this repository's build settings, that
    references the release's packages: the Dapper driver's hashed text is PostgresMigrationProvider's output, and the
    EF Core driver's is the generated initializer's own _transformMigrationSql over its embedded migrations, for a
    context in the public schema and one in the named schema the EF Core tests use. It writes one TSV per form:

        dapper-public.tsv, efcore-public.tsv, efcore-named_svc.tsv

    The app reads private members of the generated initializer through reflection; it is a capture tool, never shipped.

.PARAMETER Version
    The release whose packages to read, for example 0.2623.1.

.PARAMETER OutputDirectory
    Where to write the TSVs. Default: the Dapper test project's fixture directory for that release.

.PARAMETER Source
    The NuGet feed holding the release's packages. Default: nuget.org. A local packages folder works too, for example
    ~/.nuget/packages.

.EXAMPLE
    ./scripts/Capture-LegacyMigrationLedger.ps1 -Version 0.2623.1
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Version,
  [string] $OutputDirectory,
  [string] $Source = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
  $OutputDirectory = Join-Path $repoRoot "tests/Whizbang.Data.Dapper.Postgres.Tests/Fixtures/LegacyMigrationLedgers/v$Version"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$work = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-ledger-capture-$Version-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
  # Empty props files stop MSBuild from walking up into a parent directory's settings.
  Set-Content -Path (Join-Path $work 'Directory.Build.props') -Value '<Project />'
  Set-Content -Path (Join-Path $work 'Directory.Build.targets') -Value '<Project />'
  Set-Content -Path (Join-Path $work 'Directory.Packages.props') -Value '<Project />'
  Set-Content -Path (Join-Path $work 'nuget.config') -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="release" value="$Source" />
  </packageSources>
</configuration>
"@
  Set-Content -Path (Join-Path $work 'capture.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>`$(NoWarn);NU1603;NU1608;NU1701</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SoftwareExtravaganza.Whizbang.Data.EFCore.Postgres" Version="$Version" />
    <PackageReference Include="SoftwareExtravaganza.Whizbang.Data.EFCore.Postgres.Generators" Version="$Version" />
    <PackageReference Include="SoftwareExtravaganza.Whizbang.Data.Postgres" Version="$Version" />
  </ItemGroup>
</Project>
"@
  Set-Content -Path (Join-Path $work 'Program.cs') -Value @'
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

var outDir = args[0];
var version = args[1];
string Header(string form) =>
  $"# The migration ledger hashes Whizbang {version} recorded ({form}): file_name<TAB>content_hash.\n"
  + $"# Captured from the published {version} packages by scripts/Capture-LegacyMigrationLedger.ps1; do not edit.\n";

// Dapper: the hash the release's PostgresSchemaInitializer recorded is SHA-256 of PostgresMigrationProvider's text.
var dapper = new StringBuilder(Header("Dapper driver, schema public"));
foreach (var m in new Whizbang.Data.Postgres.PostgresMigrationProvider().GetMigrations()) {
  dapper.Append(m.Name).Append('\t').Append(Sha(m.Sql)).Append('\n');
}
File.WriteAllText(Path.Combine(outDir, "dapper-public.tsv"), dapper.ToString());

// EF Core: SHA-256 of the generated initializer's own transform of each embedded migration.
foreach (var (type, schema) in new[] { (typeof(PublicCtx), "public"), (typeof(NamedCtx), "named_svc") }) {
  var ext = type.Assembly.GetTypes().Single(t => t.Name == type.Name + "SchemaExtensions");
  var scripts = (ValueTuple<string, string>[])ext.GetMethod("GetMigrationScripts", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
  var transform = ext.GetMethod("_transformMigrationSql", BindingFlags.NonPublic | BindingFlags.Static)!;
  var sb = new StringBuilder(Header($"EF Core driver, schema {schema}"));
  foreach (var (name, sql) in scripts) {
    sb.Append(name).Append('\t').Append(Sha((string)transform.Invoke(null, [sql, schema])!)).Append('\n');
  }
  File.WriteAllText(Path.Combine(outDir, $"efcore-{schema}.tsv"), sb.ToString());
}

[WhizbangDbContext(Schema = "public")]
public partial class PublicCtx(DbContextOptions<PublicCtx> options) : DbContext(options);

[WhizbangDbContext("named-schema", Schema = "named_svc")]
public partial class NamedCtx(DbContextOptions<NamedCtx> options) : DbContext(options);
'@
  Push-Location $work
  try {
    & dotnet run --configuration Release -- $OutputDirectory $Version
    if ($LASTEXITCODE -ne 0) {
      throw "The capture app failed for $Version (exit $LASTEXITCODE)."
    }
  } finally {
    Pop-Location
  }
  Get-ChildItem $OutputDirectory -Filter '*.tsv' | ForEach-Object { Write-Host "Wrote $($_.FullName)" }
} finally {
  Remove-Item -Recurse -Force $work
}
