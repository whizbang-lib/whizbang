# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The optional refresh for .github/pipeline-tool-licenses.txt: it reads each tool's license from GitHub or
# NuGet and reports where the list disagrees. These tests never touch the network; every API call is mocked.

BeforeAll {
  . (Join-Path $PSScriptRoot '..' 'Compare-PipelineToolLicenses.ps1')
  function Entry([string]$Kind, [string]$Id, [string]$License) {
    [pscustomobject]@{ Kind = $Kind; Id = $Id; License = $License; Exception = $false; Reason = 'why'; Line = 1 }
  }
}

Describe 'Get-UpstreamLicense' {
  It "reads an action's license from its repository's GitHub license endpoint" {
    Mock Invoke-RestMethod -ParameterFilter { $Uri -eq 'https://api.github.com/repos/owner/repo/license' } -MockWith {
      [pscustomobject]@{ license = [pscustomobject]@{ spdx_id = 'Apache-2.0' } }
    }
    Get-UpstreamLicense -Entry (Entry 'action' 'owner/repo' 'MIT') | Should -Be 'Apache-2.0'
  }

  It 'sends a token when one is set, to avoid the anonymous rate limit' {
    $saved = $env:GH_TOKEN
    try {
      $env:GH_TOKEN = 'secret-token'
      Mock Invoke-RestMethod -ParameterFilter { $Headers.Authorization -eq 'Bearer secret-token' } -MockWith {
        [pscustomobject]@{ license = [pscustomobject]@{ spdx_id = 'MIT' } }
      }
      Get-UpstreamLicense -Entry (Entry 'action' 'owner/repo' 'MIT') | Should -Be 'MIT'
    } finally { $env:GH_TOKEN = $saved }
  }

  It "reads a dotnet tool's license expression from the newest version in its NuGet registration" {
    Mock Invoke-RestMethod -ParameterFilter { $Uri -like '*/registration5-gz-semver2/some.tool/index.json' } -MockWith {
      [pscustomobject]@{ items = @(
          [pscustomobject]@{ items = @([pscustomobject]@{ catalogEntry = [pscustomobject]@{ licenseExpression = 'GPL-3.0-only' } }) }
          [pscustomobject]@{ items = @(
              [pscustomobject]@{ catalogEntry = [pscustomobject]@{ licenseExpression = 'GPL-3.0-only' } }
              [pscustomobject]@{ catalogEntry = [pscustomobject]@{ licenseExpression = 'MIT' } }) }) }
    }
    Get-UpstreamLicense -Entry (Entry 'dotnet-tool' 'some.tool' 'MIT') | Should -Be 'MIT'
  }

  It 'follows a registration page that is not inlined' {
    Mock Invoke-RestMethod -ParameterFilter { $Uri -like '*/index.json' } -MockWith {
      [pscustomobject]@{ items = @([pscustomobject]@{ '@id' = 'https://api.nuget.org/page/2' }) }
    }
    Mock Invoke-RestMethod -ParameterFilter { $Uri -eq 'https://api.nuget.org/page/2' } -MockWith {
      [pscustomobject]@{ items = @([pscustomobject]@{ catalogEntry = [pscustomobject]@{ licenseExpression = 'Apache-2.0' } }) }
    }
    Get-UpstreamLicense -Entry (Entry 'dotnet-tool' 'paged.tool' 'MIT') | Should -Be 'Apache-2.0'
  }

  It 'says so when a package declares a license file or URL instead of an expression' {
    Mock Invoke-RestMethod -MockWith {
      [pscustomobject]@{ items = @([pscustomobject]@{ items = @([pscustomobject]@{ catalogEntry = [pscustomobject]@{ licenseUrl = 'https://x/license' } }) }) }
    }
    Get-UpstreamLicense -Entry (Entry 'dotnet-tool' 'file.tool' 'MIT') | Should -Be 'no SPDX expression (https://x/license)'
  }

  It 'reports a failed lookup instead of stopping the comparison' {
    Mock Invoke-RestMethod -MockWith { throw 'rate limited' }
    Get-UpstreamLicense -Entry (Entry 'action' 'owner/repo' 'MIT') | Should -BeLike 'lookup failed: *rate limited*'
  }
}

Describe 'Compare-ToolLicense' {
  It 'marks each entry whose upstream license differs from the list' {
    Mock Get-UpstreamLicense -MockWith { if ($Entry.Id -eq 'a/same') { 'MIT' } else { 'AGPL-3.0' } }
    $rows = @(Compare-ToolLicense -List @((Entry 'action' 'a/same' 'MIT'), (Entry 'action' 'a/changed' 'MIT')))
    ($rows | Where-Object Id -EQ 'a/same').Matches | Should -BeTrue
    ($rows | Where-Object Id -EQ 'a/changed').Matches | Should -BeFalse
    ($rows | Where-Object Id -EQ 'a/changed').Upstream | Should -Be 'AGPL-3.0'
  }
}
