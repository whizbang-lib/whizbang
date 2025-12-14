#!/usr/bin/env pwsh
#
# Generate-CoreSchema.ps1
# Generates core infrastructure schema SQL using PostgresSchemaBuilder
# and saves it to an embedded resource file for the EF Core generator.
#

$ErrorActionPreference = "Stop"

Write-Host "Generating core infrastructure schema SQL..." -ForegroundColor Cyan

# Build path to the schema builder assembly
$dapperProjectPath = Join-Path $PSScriptRoot ".." "Whizbang.Data.Dapper.Postgres" "Whizbang.Data.Dapper.Postgres.csproj"
$outputPath = Join-Path $PSScriptRoot "Resources" "CoreInfrastructureSchema.sql"

# Ensure the Dapper project is built
Write-Host "Building Whizbang.Data.Dapper.Postgres..." -ForegroundColor Yellow
dotnet build $dapperProjectPath --configuration Debug --no-incremental

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build Whizbang.Data.Dapper.Postgres"
    exit 1
}

# Create a temp C# script to generate the SQL
$tempScript = @"
using System;
using System.IO;
using Whizbang.Data.Schema;
using Whizbang.Data.Dapper.Postgres.Schema;

var config = new SchemaConfiguration("wh_", "wh_per_", "public", 1);
var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

var outputPath = args[0];
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, sql);

Console.WriteLine($"Generated schema SQL ({sql.Length} bytes) to: {outputPath}");
"@

$tempScriptPath = [System.IO.Path]::GetTempFileName() + ".csx"
Set-Content -Path $tempScriptPath -Value $tempScript

try {
    # Run the script using dotnet-script
    Write-Host "Generating SQL using PostgresSchemaBuilder..." -ForegroundColor Yellow

    # Use dotnet run with a temporary project
    $tempProjectDir = Join-Path ([System.IO.Path]::GetTempPath()) "SchemaGenerator_$(Get-Random)"
    New-Item -ItemType Directory -Path $tempProjectDir -Force | Out-Null

    $tempCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$($dapperProjectPath)" />
  </ItemGroup>
</Project>
"@

    $tempProgramCs = @"
using System;
using System.IO;
using Whizbang.Data.Schema;
using Whizbang.Data.Dapper.Postgres.Schema;

var config = new SchemaConfiguration("wh_", "wh_per_", "public", 1);
var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

var outputPath = args[0];
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, sql);

Console.WriteLine($"Generated schema SQL ({sql.Length} bytes) to: {outputPath}");
"@

    Set-Content -Path (Join-Path $tempProjectDir "Program.cs") -Value $tempProgramCs
    Set-Content -Path (Join-Path $tempProjectDir "temp.csproj") -Value $tempCsproj

    dotnet run --project (Join-Path $tempProjectDir "temp.csproj") -- $outputPath

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to generate schema SQL"
        exit 1
    }

    Write-Host "✓ Schema SQL generated successfully!" -ForegroundColor Green
    Write-Host "  Output: $outputPath" -ForegroundColor Gray
} finally {
    # Clean up temp files
    if (Test-Path $tempScriptPath) {
        Remove-Item $tempScriptPath -Force
    }
    if (Test-Path $tempProjectDir) {
        Remove-Item $tempProjectDir -Recurse -Force
    }
}
