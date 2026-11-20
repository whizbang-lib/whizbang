# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# #2422: SonarScanner begin downloads a JRE and the scanner engine into its cache and names them in the analysis
# configuration by absolute path. Re-running only the failed jobs re-runs the job that ends the scan on a fresh
# runner, without the job that began it, and the end step then could not find either. These tests fail if the
# analysis state stops carrying what begin downloaded, or if a missing download goes unexplained.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Copy-SonarScannerCache.ps1'
  . $script:Script

  # The analysis configuration begin writes (.sonarqube/conf/SonarQubeAnalysisConfig.xml), reduced to the paths.
  function New-Config([string]$Root) {
    @"
<?xml version="1.0" encoding="utf-8"?>
<AnalysisConfig xmlns="http://www.sonarsource.com/msbuild/integration/2015/1">
  <SonarConfigDir>/w/.sonarqube/conf</SonarConfigDir>
  <JavaExePath>$Root/a1b2/OpenJDK21U-jre_x64_linux_hotspot_21.tar.gz_extracted/jdk-21-jre/bin/java</JavaExePath>
  <EngineJarPath>$Root/c3d4/sonarcloud-scanner-engine-13.19.0.6869.jar</EngineJarPath>
  <LocalSettings>
    <Property Name="sonar.java.binaries">$Root/a1b2/OpenJDK21U-jre_x64_linux_hotspot_21.tar.gz_extracted/jdk-21-jre/lib</Property>
    <Property Name="sonar.other">/elsewhere/.sonar/cache/zzzz/ignored.jar</Property>
  </LocalSettings>
</AnalysisConfig>
"@
  }

  # A cache holding what begin downloaded, plus an entry the configuration does not name.
  function New-Cache([string]$Root) {
    $java = Join-Path $Root 'a1b2/OpenJDK21U-jre_x64_linux_hotspot_21.tar.gz_extracted/jdk-21-jre/bin'
    New-Item -ItemType Directory -Path $java -Force | Out-Null
    Set-Content -Path (Join-Path $java 'java') -Value 'jre'
    New-Item -ItemType Directory -Path (Join-Path $Root 'c3d4') -Force | Out-Null
    Set-Content -Path (Join-Path $Root 'c3d4/sonarcloud-scanner-engine-13.19.0.6869.jar') -Value 'engine'
    New-Item -ItemType Directory -Path (Join-Path $Root 'unrelated') -Force | Out-Null
    Set-Content -Path (Join-Path $Root 'unrelated/plugin.jar') -Value 'plugin'
  }
}

Describe 'Get-SonarCacheEntry' {
  It 'names each cache entry the configuration points into, once, and nothing outside the cache' {
    Get-SonarCacheEntry -ConfigText (New-Config '/home/runner/.sonar/cache') -CacheRoot '/home/runner/.sonar/cache/' |
      Should -Be @('a1b2', 'c3d4')
  }

  It 'names nothing when the configuration points into no cache entry' {
    @(Get-SonarCacheEntry -ConfigText '<AnalysisConfig />' -CacheRoot '/home/runner/.sonar/cache') | Should -HaveCount 0
  }
}

Describe 'the script' {
  BeforeEach {
    $script:Began = Join-Path $TestDrive 'began/.sonar/cache'
    $script:State = Join-Path $TestDrive 'state'
    $script:Config = Join-Path $TestDrive 'SonarQubeAnalysisConfig.xml'
    Remove-Item -Recurse -Force -Path (Join-Path $TestDrive 'began'), $script:State -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $script:State -Force | Out-Null
    Set-Content -Path $script:Config -Value (New-Config $script:Began)
  }

  It 'saves what begin downloaded, and restores it where the configuration names it on a runner without it' {
    New-Cache $script:Began
    & $script:Script -Action Save -ConfigPath $script:Config -CacheRoot $script:Began -StateDir $script:State | Out-Null
    $LASTEXITCODE | Should -Be 0

    # The runner that ends the scan has never seen the cache.
    Remove-Item -Recurse -Force -Path (Join-Path $TestDrive 'began')
    $out = & $script:Script -Action Restore -ConfigPath $script:Config -StateDir $script:State
    $LASTEXITCODE | Should -Be 0
    "$out" | Should -BeLike '*a1b2*c3d4*'

    Get-Content (Join-Path $script:Began 'a1b2/OpenJDK21U-jre_x64_linux_hotspot_21.tar.gz_extracted/jdk-21-jre/bin/java') | Should -Be 'jre'
    Get-Content (Join-Path $script:Began 'c3d4/sonarcloud-scanner-engine-13.19.0.6869.jar') | Should -Be 'engine'
    Join-Path $script:Began 'unrelated' | Should -Not -Exist -Because 'only what the configuration names travels with the state'
  }

  It 'leaves a cache that already holds every entry as it is' {
    New-Cache $script:Began
    & $script:Script -Action Save -ConfigPath $script:Config -CacheRoot $script:Began -StateDir $script:State | Out-Null
    Set-Content -Path (Join-Path $script:Began 'c3d4/sonarcloud-scanner-engine-13.19.0.6869.jar') -Value 'local'

    $out = & $script:Script -Action Restore -ConfigPath $script:Config -StateDir $script:State
    $LASTEXITCODE | Should -Be 0
    "$out" | Should -BeLike '*already*'
    Get-Content (Join-Path $script:Began 'c3d4/sonarcloud-scanner-engine-13.19.0.6869.jar') | Should -Be 'local'
  }

  It 'refuses to save when the configuration names an entry begin did not leave in the cache' {
    New-Cache $script:Began
    Remove-Item -Recurse -Force -Path (Join-Path $script:Began 'c3d4')

    $out = & $script:Script -Action Save -ConfigPath $script:Config -CacheRoot $script:Began -StateDir $script:State 2>&1
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '*c3d4*'
  }

  It 'saves nothing to carry when the configuration names no cache entry, and restores that as nothing to do' {
    Set-Content -Path $script:Config -Value '<AnalysisConfig />'
    & $script:Script -Action Save -ConfigPath $script:Config -CacheRoot $script:Began -StateDir $script:State | Out-Null
    $LASTEXITCODE | Should -Be 0
    Join-Path $script:State 'scanner-cache.tgz' | Should -Not -Exist

    $out = & $script:Script -Action Restore -ConfigPath $script:Config -StateDir $script:State
    $LASTEXITCODE | Should -Be 0
    "$out" | Should -BeLike '*names nothing*'
  }

  It 'fails, saying a full re-run is needed, when the state carries no cache and the runner lacks it' {
    # A state saved before the cache traveled with it: the configuration names entries nobody can supply.
    Set-Content -Path (Join-Path $script:State 'scanner-cache-root.txt') -Value $script:Began -NoNewline

    $out = & $script:Script -Action Restore -ConfigPath $script:Config -StateDir $script:State 2>&1
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '*a1b2*full re-run*'
  }

  It 'fails, naming the file, when the configuration does not exist' {
    $out = & $script:Script -Action Restore -ConfigPath (Join-Path $TestDrive 'none.xml') -StateDir $script:State 2>&1
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '*none.xml*'
  }
}
