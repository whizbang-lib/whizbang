# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The pipeline and build files are owned by the build-admins team (.github/CODEOWNERS), and the
# Gate · Pipeline owner check requires a build admin's approval of a change to them by anyone else, so
# only a build admin can change them (GOVERNANCE.md, .github/scripts/Test-PipelineOwner.ps1).

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Entries = @(Get-Content (Join-Path $Root '.github' 'CODEOWNERS') |
      Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } |
      ForEach-Object { $parts = $_ -split '\s+'; [pscustomobject]@{ Pattern = $parts[0]; Owners = @($parts[1..($parts.Count - 1)]) } })
}

Describe 'CODEOWNERS' {
  It 'gives every pattern to the build-admins team only' {
    $Entries.Count | Should -BeGreaterThan 0
    $Entries | ForEach-Object { $_.Owners | Should -Be @('@whizbang-lib/build-admins') }
  }

  It 'covers the workflows, the pipeline scripts and itself' -TestCases @(
    @{ Path = '/.github/' }, @{ Path = '/scripts/' }
  ) {
    $Entries.Pattern | Should -Contain $Path
  }

  It 'names only paths that exist, so a rename cannot leave a file unowned' {
    foreach ($e in $Entries) {
      $relative = $e.Pattern.TrimStart('/')
      $found = @(Get-ChildItem -Path (Join-Path $Root $relative) -Force -ErrorAction SilentlyContinue)
      $found.Count | Should -BeGreaterThan 0 -Because "$($e.Pattern) should match a file in the repository"
    }
  }
}
