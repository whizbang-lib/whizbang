# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Set-SonarPullRequest.ps1')

  # The shape SonarScanner begin writes (.sonarqube/conf/SonarQubeAnalysisConfig.xml), reduced.
  function New-Config([string]$Key = '101', [string]$Branch = 'feature/a', [string]$Namespace = 'http://www.sonarsource.com/msbuild/integration/2015/1') {
    $ns = if ($Namespace) { " xmlns=""$Namespace""" } else { '' }
    [xml]@"
<?xml version="1.0" encoding="utf-8"?>
<AnalysisConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema"$ns>
  <SonarConfigDir>/w/.sonarqube/conf</SonarConfigDir>
  <LocalSettings>
    <Property Name="sonar.host.url">https://sonarcloud.io</Property>
    <Property Name="sonar.pullrequest.key">$Key</Property>
    <Property Name="sonar.pullrequest.branch">$Branch</Property>
    <Property Name="sonar.pullrequest.base">develop</Property>
  </LocalSettings>
</AnalysisConfig>
"@
  }

  function Get-Prop([xml]$Xml, [string]$Name) {
    @($Xml.SelectNodes("//*[local-name()='Property' and @Name='$Name']") | ForEach-Object { $_.InnerText })
  }
}

Describe 'Set-SonarPullRequestProperty' {
  It 'points the analysis at another pull request, leaving every other setting as it was' {
    $x = New-Config
    Set-SonarPullRequestProperty -Xml $x -Key '205' -Branch 'fix/b'
    Get-Prop $x 'sonar.pullrequest.key' | Should -Be @('205')
    Get-Prop $x 'sonar.pullrequest.branch' | Should -Be @('fix/b')
    Get-Prop $x 'sonar.pullrequest.base' | Should -Be @('develop')
    Get-Prop $x 'sonar.host.url' | Should -Be @('https://sonarcloud.io')
  }

  It 'works without the XML namespace too' {
    $x = New-Config -Namespace ''
    Set-SonarPullRequestProperty -Xml $x -Key '9' -Branch 'b'
    Get-Prop $x 'sonar.pullrequest.key' | Should -Be @('9')
  }

  It 'refuses a configuration that is not a pull-request analysis, rather than turning it into one' {
    $x = New-Config
    $x.SelectNodes("//*[local-name()='Property' and @Name='sonar.pullrequest.key']") | ForEach-Object { [void]$_.ParentNode.RemoveChild($_) }
    { Set-SonarPullRequestProperty -Xml $x -Key '1' -Branch 'b' } | Should -Throw '*not a pull-request analysis*'
  }

  It 'refuses an empty key or branch' {
    { Set-SonarPullRequestProperty -Xml (New-Config) -Key '' -Branch 'b' } | Should -Throw
    { Set-SonarPullRequestProperty -Xml (New-Config) -Key '1' -Branch '' } | Should -Throw
  }
}

Describe 'the script' {
  It 'rewrites the file in place and leaves it a valid configuration' {
    $path = Join-Path $TestDrive 'SonarQubeAnalysisConfig.xml'
    (New-Config).Save($path)
    & (Join-Path $PSScriptRoot '..' 'Set-SonarPullRequest.ps1') -ConfigPath $path -Key '77' -Branch 'feat/c' | Out-Null
    $LASTEXITCODE | Should -Be 0
    $saved = [xml](Get-Content -Raw $path)
    Get-Prop $saved 'sonar.pullrequest.key' | Should -Be @('77')
    Get-Prop $saved 'sonar.pullrequest.branch' | Should -Be @('feat/c')
  }

  It 'fails, naming the file, when it does not exist' {
    $out = & (Join-Path $PSScriptRoot '..' 'Set-SonarPullRequest.ps1') -ConfigPath (Join-Path $TestDrive 'none.xml') -Key '1' -Branch 'b' 2>&1
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '*none.xml*'
  }
}
