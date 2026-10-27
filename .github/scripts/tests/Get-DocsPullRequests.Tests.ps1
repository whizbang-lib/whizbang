# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Get-DocsPullRequests.ps1') -Repository o/code -PullRequest 0
  $script:Docs = 'o/docs'
}

Describe 'Get-DocsLinkNumbers' {
  It 'reads docs-site pull requests linked by URL or by reference, once each' {
    $body = "Docs: https://github.com/o/docs/pull/12`nAlso o/docs#13 and o/docs#12. Not o/code#14 or o/docs-old#15."
    @(Get-DocsLinkNumbers -Body $body -DocsRepository $Docs) | Should -Be @(12, 13)
  }

  It 'reads nothing from an empty description' {
    @(Get-DocsLinkNumbers -Body '' -DocsRepository $Docs) | Should -BeNullOrEmpty
  }
}

Describe 'Test-ReferencesPullRequest' {
  It 'finds a reference to the code pull request by reference or URL' -TestCases @(
    @{ Body = 'Docs for o/code#1458' }
    @{ Body = 'See https://github.com/o/code/pull/1458.' }
  ) {
    Test-ReferencesPullRequest -Body $Body -Repository 'o/code' -Number 1458 | Should -BeTrue
  }

  It 'does not mistake another number or another repository' -TestCases @(
    @{ Body = 'Docs for o/code#14580' }
    @{ Body = 'Docs for o/code-extra#1458' }
    @{ Body = 'Docs for #1458' }
  ) {
    Test-ReferencesPullRequest -Body $Body -Repository 'o/code' -Number 1458 | Should -BeFalse
  }
}

Describe 'ConvertFrom-DocsCrossReferences' {
  It 'reads the docs-site pull requests that mention this one, from its timeline' {
    $json = @'
{"data":{"repository":{"pullRequest":{"body":"","timelineItems":{"nodes":[
  {"source":{"number":21,"state":"OPEN","updatedAt":"2026-10-27T03:40:17Z","repository":{"nameWithOwner":"o/docs"}}},
  {"source":{"number":5,"state":"OPEN","updatedAt":"2026-10-27T01:00:00Z","repository":{"nameWithOwner":"o/other"}}},
  {"source":{"number":22,"state":"MERGED","updatedAt":"2026-10-26T09:00:00Z","repository":{"nameWithOwner":"o/docs"}}},
  {"source":{"number":21,"state":"OPEN","updatedAt":"2026-10-27T03:40:17Z","repository":{"nameWithOwner":"o/docs"}}},
  {"source":{}}]}}}}}
'@
    $refs = @(ConvertFrom-DocsCrossReferences -Json $json -DocsRepository $Docs)
    $refs.Number | Should -Be @(21, 22)
    $refs.State | Should -Be @('OPEN', 'MERGED')
    $refs.ReferencesThis | Should -Be @($true, $true)
    $refs.UpdatedAt | Should -Be @([datetimeoffset]'2026-10-27T03:40:17Z', [datetimeoffset]'2026-10-26T09:00:00Z')
  }
}

Describe 'Merge-DocsPullRequests' {
  It 'keeps the cross-referenced ones and adds the description links, checked for a link back' {
    $cross = @([pscustomobject]@{ Number = 21; State = 'OPEN'; ReferencesThis = $true })
    $lookup = { param([int]$N) [pscustomobject]@{ Number = $N; State = 'OPEN'; Body = $(if ($N -eq 30) { 'Docs for o/code#7' } else { 'nothing' })
        UpdatedAt = [datetimeoffset]"2026-10-27T00:00:$($N)Z" } }
    $all = @(Merge-DocsPullRequests -CrossReferenced $cross -LinkedNumbers @(21, 30, 31) -Repository 'o/code' -Number 7 -LookupDocsPullRequest $lookup)
    $all.Number | Should -Be @(21, 30, 31)
    $all.ReferencesThis | Should -Be @($true, $true, $false)
    $all[1].UpdatedAt | Should -Be ([datetimeoffset]'2026-10-27T00:00:30Z')
  }

  It 'skips a description link that is not a pull request' {
    $all = @(Merge-DocsPullRequests -CrossReferenced @() -LinkedNumbers @(40) -Repository 'o/code' -Number 7 -LookupDocsPullRequest { param([int]$N) $null })
    $all.Count | Should -Be 0
  }
}

Describe 'ConvertTo-DocsPullRequestsJson' {
  It 'writes each docs pull request with when it last changed, so a re-check can tell a stale review check' {
    $docs = @(
      [pscustomobject]@{ Number = 21; State = 'OPEN'; ReferencesThis = $true; UpdatedAt = [datetimeoffset]'2026-10-27T03:40:17Z' }
      [pscustomobject]@{ Number = 22; State = 'CLOSED'; ReferencesThis = $false; UpdatedAt = $null }
    )
    $read = ConvertTo-DocsPullRequestsJson -DocsPullRequests $docs | ConvertFrom-Json -DateKind String
    $read.number | Should -Be @(21, 22)
    $read.state | Should -Be @('OPEN', 'CLOSED')
    $read.referencesThis | Should -Be @($true, $false)
    $read[0].updatedAt | Should -Be '2026-10-27T03:40:17.0000000+00:00'
    $read[1].updatedAt | Should -BeNullOrEmpty
  }

  It 'writes an empty list when there are none' {
    ConvertTo-DocsPullRequestsJson -DocsPullRequests @() | Should -Be '[]'
  }
}
