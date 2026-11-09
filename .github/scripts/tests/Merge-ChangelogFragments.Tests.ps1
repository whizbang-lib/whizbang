# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The changelog gather (#2168): every pull request into develop writes its entry as a fragment in changelog.d/,
# and cutting a release renders the fragments, plus whatever is still under [Unreleased], into the version's
# section of CHANGELOG.md and deletes exactly the fragments it gathered.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Merge-ChangelogFragments.ps1'
  . $script:ScriptPath -Version 0.0.0
  $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

  $script:Header = @(
    '# Changelog', '', 'All notable changes to this project will be documented in this file.', ''
  )

  # A repository root holding CHANGELOG.md (header, [Unreleased] with the given lines, one old release) and
  # changelog.d/ with a README and the given fragments (name -> text).
  function New-ChangelogRoot([string[]]$Unreleased = @(), [hashtable]$Fragments = @{}) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("clg-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $work 'changelog.d') -Force | Out-Null
    $lines = @($script:Header) + @('## [Unreleased]', '') + $Unreleased + @('', '## [0.1.0-alpha] - 2026-01-19', '', '### Added', '- Initial alpha release')
    Set-Content -Path (Join-Path $work 'CHANGELOG.md') -Value (($lines -join "`n") + "`n") -NoNewline
    Set-Content -Path (Join-Path $work 'changelog.d' 'README.md') -Value "# Changelog fragments`n" -NoNewline
    foreach ($name in $Fragments.Keys) {
      Set-Content -Path (Join-Path $work 'changelog.d' $name) -Value $Fragments[$name] -NoNewline
    }
    return $work
  }

  function New-Fragment([string]$Category, [string]$Subsection = '', [string[]]$Body) {
    $front = @('---', "category: $Category")
    if ($Subsection) { $front += "subsection: $Subsection" }
    $front += '---'
    return ((@($front) + $Body) -join "`n") + "`n"
  }

  function Get-Changelog([string]$Dir) { Get-Content -Raw (Join-Path $Dir 'CHANGELOG.md') }
  function Get-FragmentName([string]$Dir) { @(Get-ChildItem -File (Join-Path $Dir 'changelog.d') | ForEach-Object Name | Sort-Object) }
}

Describe 'Read-ChangelogFragment' {
  BeforeEach { $script:work = New-ChangelogRoot }
  AfterEach { Remove-Item -Recurse -Force $work }

  It 'reads the category, subsection, number and body' {
    $path = Join-Path $work 'changelog.d' '2152.md'
    Set-Content $path (New-Fragment -Category Added -Subsection Storage -Body @('- **A thing (#2152).** It does', '  this.')) -NoNewline
    $f = Read-ChangelogFragment -Path $path
    $f.Name | Should -Be '2152.md'
    $f.Number | Should -Be 2152
    $f.Category | Should -Be 'Added'
    $f.Subsection | Should -Be 'Storage'
    $f.Body | Should -Be @('- **A thing (#2152).** It does', '  this.')
  }

  It 'takes a range of numbers in the name and sorts by the first' {
    $path = Join-Path $work 'changelog.d' '2152-2154.md'
    Set-Content $path (New-Fragment -Category Fixed -Body @('- Fixed.')) -NoNewline
    $f = Read-ChangelogFragment -Path $path
    $f.Number | Should -Be 2152
    $f.Subsection | Should -Be ''
  }

  It 'accepts the category and subsection in any letter case and writes the documented spelling' {
    $path = Join-Path $work 'changelog.d' '7.md'
    Set-Content $path "---`ncategory: fixed`nsubsection: messaging & delivery`n---`n- x`n" -NoNewline
    $f = Read-ChangelogFragment -Path $path
    $f.Category | Should -Be 'Fixed'
    $f.Subsection | Should -Be 'Messaging & delivery'
  }

  It 'reads a fragment written with Windows line endings' {
    $path = Join-Path $work 'changelog.d' '8.md'
    Set-Content $path "---`r`ncategory: Changed`r`n---`r`n- x`r`n" -NoNewline
    (Read-ChangelogFragment -Path $path).Body | Should -Be @('- x')
  }

  It 'refuses <why>, naming the file' -ForEach @(
    @{ why = 'a name that is not a number'; name = 'storage-fix.md'; text = "---`ncategory: Added`n---`n- x`n"; msg = '*storage-fix.md*issue or pull request number*' }
    @{ why = 'a file that is not markdown'; name = '12.txt'; text = "---`ncategory: Added`n---`n- x`n"; msg = '*12.txt*issue or pull request number*' }
    @{ why = 'no front matter'; name = '12.md'; text = "- x`n"; msg = '*12.md*front matter*' }
    @{ why = 'unclosed front matter'; name = '12.md'; text = "---`ncategory: Added`n- x`n"; msg = '*12.md*front matter*' }
    @{ why = 'no category'; name = '12.md'; text = "---`nsubsection: Storage`n---`n- x`n"; msg = '*12.md*category*' }
    @{ why = 'an unknown category'; name = '12.md'; text = "---`ncategory: Improved`n---`n- x`n"; msg = "*12.md*unknown category 'Improved'*Added, Changed*" }
    @{ why = 'an unknown subsection'; name = '12.md'; text = "---`ncategory: Added`nsubsection: Misc`n---`n- x`n"; msg = "*12.md*unknown subsection 'Misc'*Storage*" }
    @{ why = 'an unknown key'; name = '12.md'; text = "---`ncategory: Added`narea: Storage`n---`n- x`n"; msg = "*12.md*unknown key 'area'*" }
    @{ why = 'a line that is not key: value'; name = '12.md'; text = "---`ncategory Added`n---`n- x`n"; msg = '*12.md*key: value*' }
    @{ why = 'an empty body'; name = '12.md'; text = "---`ncategory: Added`n---`n`n"; msg = '*12.md*no entry*' }
    @{ why = 'a body that is not a list item'; name = '12.md'; text = "---`ncategory: Added`n---`nSome prose.`n"; msg = '*12.md*list item*' }
    @{ why = 'a heading in the body'; name = '12.md'; text = "---`ncategory: Added`n---`n- x`n### Fixed`n- y`n"; msg = '*12.md*heading*' }
  ) {
    $path = Join-Path $work 'changelog.d' $name
    Set-Content $path $text -NoNewline
    { Read-ChangelogFragment -Path $path } | Should -Throw $msg
  }
}

Describe 'Merge-ChangelogFragments' {
  AfterEach { if ($script:work) { Remove-Item -Recurse -Force $script:work; $script:work = $null } }

  It 'orders by category, then subsection, then number, and keeps the old releases below' {
    $script:work = New-ChangelogRoot -Fragments @{
      '30.md' = New-Fragment -Category Fixed -Body @('- fix 30')
      '5.md' = New-Fragment -Category Fixed -Body @('- fix 5')
      '100.md' = New-Fragment -Category Added -Subsection Storage -Body @('- storage 100')
      '20.md' = New-Fragment -Category Added -Subsection Storage -Body @('- storage 20')
      '40.md' = New-Fragment -Category Added -Subsection Security -Body @('- security 40')
      '50.md' = New-Fragment -Category Added -Body @('- plain added 50')
      '60.md' = New-Fragment -Category Changed -Body @('- changed 60', '  continued.')
      '70.md' = New-Fragment -Category Security -Body @('- GHSA-xxxx-yyyy-zzzz')
    }
    $r = Merge-ChangelogFragments -Version 1.2.3 -Date 2026-11-08 -Root $work

    $expected = @(
      '## [1.2.3] - 2026-11-08', '',
      '### Added', '',
      '- plain added 50', '',
      '#### Security', '- security 40', '',
      '#### Storage', '- storage 20', '- storage 100', '',
      '### Changed', '',
      '- changed 60', '  continued.', '',
      '### Fixed', '',
      '- fix 5', '- fix 30', '',
      '### Security', '',
      '- GHSA-xxxx-yyyy-zzzz'
    )
    $r.Section | Should -Be $expected
    $text = Get-Changelog $work
    $text.StartsWith((($script:Header + @('## [Unreleased]', '') + $expected + @('', '## [0.1.0-alpha] - 2026-01-19')) -join "`n")) | Should -BeTrue
    $text | Should -Match '(?m)^- Initial alpha release\n\z'
  }

  It 'merges what is still under [Unreleased], before the fragments of the same group' {
    $unreleased = @(
      '### Added', '', '#### Storage', '- old storage', '  wrapped.', '', '#### Security', '- old security', '',
      '### Fixed', '- old fix'
    )
    $script:work = New-ChangelogRoot -Unreleased $unreleased -Fragments @{
      '9.md' = New-Fragment -Category Added -Subsection Storage -Body @('- new storage 9')
      '3.md' = New-Fragment -Category Fixed -Body @('- new fix 3')
    }
    $r = Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work

    $r.Section | Should -Be @(
      '## [1.0.0] - 2026-11-08', '',
      '### Added', '',
      '#### Security', '- old security', '',
      '#### Storage', '- old storage', '  wrapped.', '- new storage 9', '',
      '### Fixed', '',
      '- old fix', '- new fix 3'
    )
    $r.LeftoverLineCount | Should -Be 4
    # [Unreleased] is left as an empty heading.
    Get-Changelog $work | Should -Match '(?m)^## \[Unreleased\]\n\n## \[1\.0\.0\] - 2026-11-08\n'
  }

  It 'keeps text under [Unreleased] that precedes the first category' {
    $script:work = New-ChangelogRoot -Unreleased @('A note about this release.', '', '### Fixed', '- old fix')
    (Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work).Section |
      Should -Be @('## [1.0.0] - 2026-11-08', '', 'A note about this release.', '', '### Fixed', '', '- old fix')
  }

  It 'writes a section that says so when there is nothing to gather' {
    $script:work = New-ChangelogRoot
    $r = Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work
    $r.Section | Should -Be @('## [1.0.0] - 2026-11-08', '', 'No notable changes.')
    $r.Gathered.Count | Should -Be 0
    Get-FragmentName $work | Should -Be @('README.md')
  }

  It 'deletes exactly the fragments it gathered and leaves the README' {
    $script:work = New-ChangelogRoot -Fragments @{
      '1.md' = New-Fragment -Category Fixed -Body @('- one')
      '2-3.md' = New-Fragment -Category Fixed -Body @('- two')
    }
    $r = Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work
    @($r.Gathered | ForEach-Object { Split-Path -Leaf $_ }) | Should -Be @('1.md', '2-3.md')
    Get-FragmentName $work | Should -Be @('README.md')
  }

  It 'leaves a fragment added after the gather for the next release' {
    $script:work = New-ChangelogRoot -Fragments @{ '1.md' = New-Fragment -Category Fixed -Body @('- one') }
    Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work | Out-Null
    Set-Content (Join-Path $work 'changelog.d' '2.md') (New-Fragment -Category Fixed -Body @('- two')) -NoNewline

    Get-FragmentName $work | Should -Be @('2.md', 'README.md')
    $r = Merge-ChangelogFragments -Version 1.1.0 -Date 2026-11-09 -Root $work
    $r.Section | Should -Be @('## [1.1.0] - 2026-11-09', '', '### Fixed', '', '- two')
    Get-Changelog $work | Should -Match '(?s)## \[1\.1\.0\] - 2026-11-09.*- two.*## \[1\.0\.0\] - 2026-11-08.*- one'
  }

  It 'with -DryRun returns the section and changes nothing' {
    $script:work = New-ChangelogRoot -Unreleased @('### Fixed', '- old fix') -Fragments @{ '1.md' = New-Fragment -Category Fixed -Body @('- one') }
    $before = Get-Changelog $work
    $r = Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work -DryRun
    $r.Section | Should -Be @('## [1.0.0] - 2026-11-08', '', '### Fixed', '', '- old fix', '- one')
    Get-Changelog $work | Should -BeExactly $before
    Get-FragmentName $work | Should -Be @('1.md', 'README.md')
  }

  It 'refuses a malformed fragment, naming it, and changes nothing' {
    $script:work = New-ChangelogRoot -Fragments @{
      '1.md' = New-Fragment -Category Fixed -Body @('- one')
      '2.md' = New-Fragment -Category Improved -Body @('- two')
    }
    $before = Get-Changelog $work
    { Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work } | Should -Throw '*2.md*Improved*'
    Get-Changelog $work | Should -BeExactly $before
    Get-FragmentName $work | Should -Be @('1.md', '2.md', 'README.md')
  }

  It 'refuses an unknown heading left under [Unreleased]' {
    $script:work = New-ChangelogRoot -Unreleased @('### Improved', '- x')
    { Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work } | Should -Throw "*CHANGELOG.md*unknown category 'Improved'*"
    $script:work2 = New-ChangelogRoot -Unreleased @('### Added', '#### Misc', '- x')
    try {
      { Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work2 } | Should -Throw "*CHANGELOG.md*unknown subsection 'Misc'*"
    } finally { Remove-Item -Recurse -Force $work2 }
  }

  It 'refuses a version that already has a section' {
    $script:work = New-ChangelogRoot
    { Merge-ChangelogFragments -Version 0.1.0-alpha -Date 2026-11-08 -Root $work } | Should -Throw '*0.1.0-alpha*already*'
  }

  It 'refuses a CHANGELOG.md with no [Unreleased] heading' {
    $script:work = New-ChangelogRoot
    Set-Content (Join-Path $work 'CHANGELOG.md') "# Changelog`n`n## [0.1.0] - 2026-01-01`n" -NoNewline
    { Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work } | Should -Throw '*Unreleased*'
  }

  It 'refuses a date that is not yyyy-MM-dd' {
    $script:work = New-ChangelogRoot
    { Merge-ChangelogFragments -Version 1.0.0 -Date 11/08/2026 -Root $work } | Should -Throw '*yyyy-MM-dd*'
  }

  It 'reads CHANGELOG.md written with Windows line endings and writes it back with LF' {
    $script:work = New-ChangelogRoot -Unreleased @('### Fixed', '- old fix')
    $path = Join-Path $work 'CHANGELOG.md'
    Set-Content $path ((Get-Content -Raw $path) -replace "`n", "`r`n") -NoNewline
    (Merge-ChangelogFragments -Version 1.0.0 -Date 2026-11-08 -Root $work).Section | Should -Be @('## [1.0.0] - 2026-11-08', '', '### Fixed', '', '- old fix')
    Get-Changelog $work | Should -Not -Match "`r"
  }
}

Describe 'Merge-ChangelogFragments.ps1 run as a script' {
  BeforeEach {
    $script:work = New-ChangelogRoot -Fragments @{ '1.md' = New-Fragment -Category Fixed -Body @('- one') }
  }
  AfterEach { Remove-Item -Recurse -Force $work }

  It '-DryRun prints the section and writes nothing' {
    $out = & pwsh -NoProfile -File $ScriptPath -Version 1.0.0 -Date 2026-11-08 -Root $work -DryRun
    $LASTEXITCODE | Should -Be 0
    ($out -join "`n") | Should -Match '(?s)## \[1\.0\.0\] - 2026-11-08.*- one'
    Get-FragmentName $work | Should -Be @('1.md', 'README.md')
  }

  It 'gathers, writes the step summary, and reports what it did' {
    $summary = Join-Path $work 'summary.md'
    $env:GITHUB_STEP_SUMMARY = $summary
    try {
      $out = & pwsh -NoProfile -File $ScriptPath -Version 1.0.0 -Date 2026-11-08 -Root $work
    } finally { Remove-Item Env:GITHUB_STEP_SUMMARY }
    $LASTEXITCODE | Should -Be 0
    ($out -join "`n") | Should -Match 'Gathered 1 fragment'
    Get-FragmentName $work | Should -Be @('README.md')
    Get-Content -Raw $summary | Should -Match '(?s)## \[1\.0\.0\] - 2026-11-08.*- one'
  }

  It 'fails with an error annotation naming a malformed fragment' {
    Set-Content (Join-Path $work 'changelog.d' 'oops.md') 'x' -NoNewline
    $out = & pwsh -NoProfile -File $ScriptPath -Version 1.0.0 -Root $work -DryRun
    $LASTEXITCODE | Should -Be 1
    ($out -join "`n") | Should -Match '::error::.*oops\.md'
  }
}

# The format is documented once, in changelog.d/README.md, and these lock the README to the rules: a category
# or subsection the script accepts is one the README lists, and the other way round.
Describe 'changelog.d in the repository' {
  It 'documents every category and subsection the gather accepts, and no other' {
    $readme = Get-Content -Raw (Join-Path $RepoRoot 'changelog.d' 'README.md')
    $listed = { param($heading) @([regex]::Match($readme, "(?ms)^## $heading\s*\n(.*?)(?=^## |\z)").Groups[1].Value -split "`n" |
          Where-Object { $_ -match '^- `([^`]+)`' } | ForEach-Object { [regex]::Match($_, '^- `([^`]+)`').Groups[1].Value }) }
    & $listed 'Categories' | Should -Be $script:ChangelogCategories
    & $listed 'Subsections' | Should -Be $script:ChangelogSubsections
  }

  It 'holds only fragments the gather can read' {
    $result = Merge-ChangelogFragments -Version 999.0.0 -Date 2026-11-08 -Root $RepoRoot -DryRun
    $result.Section[0] | Should -Be '## [999.0.0] - 2026-11-08'
  }
}

# A function no workflow calls decides nothing. These lock the wiring: the cut gathers the changelog into the
# same commit as the version bump, and the cut's reuse of develop's tests still accepts that commit.
Describe 'The release cut gathers the changelog' {
  BeforeAll {
    $wf = Join-Path $PSScriptRoot '..' '..' 'workflows'
    $script:StartRelease = Get-Content -Raw (Join-Path $wf 'start-release.yml')
    $script:Ci = Get-Content -Raw (Join-Path $wf 'ci.yml')
  }

  It 'Start Release gathers after the version bump and commits both in the one bump commit' {
    $StartRelease | Should -Match '(?s)Update Version in Directory\.Build\.props.*Merge-ChangelogFragments\.ps1 -Version "\$VERSION".*git add -A -- Directory\.Build\.props CHANGELOG\.md changelog\.d\s*\n\s*git commit -m "chore\(release\): bump version'
  }

  It 'the release cut reuses the develop tests when only the version file and the changelog differ' {
    $Ci | Should -Match 'select\(\. != "Directory\.Build\.props" and \. != "CHANGELOG\.md" and \(startswith\("changelog\.d/"\) \| not\)\)'
  }
}
