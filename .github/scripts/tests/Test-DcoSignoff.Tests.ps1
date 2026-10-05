#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-DcoSignoff.ps1') -Base x -Head y

  $script:Adopted = [datetimeoffset]'2026-10-04T00:00:00Z'

  # One commit as Get-PullRequestCommit returns it. Tests override from here.
  function New-TestCommit {
    param(
        [string]$Message = "feat: a change`n`nSigned-off-by: Dana Doe <dana@example.com>",
        [string]$AuthorName = 'Dana Doe',
        [string]$AuthorEmail = 'dana@example.com',
        [string]$Date = '2026-10-05T12:00:00+00:00',
        [int]$Parents = 1
    )
    [pscustomobject]@{
      Sha = ('a' * 40); Parents = $Parents; AuthorName = $AuthorName; AuthorEmail = $AuthorEmail
      AuthorDate = [datetimeoffset]$Date; Message = $Message
    }
  }
}

Describe 'Get-DcoVerdict' {
  It 'passes a commit signed off by its author' {
    (Get-DcoVerdict -Commits @(New-TestCommit) -AdoptedOn $Adopted).Pass | Should -BeTrue
  }

  It 'fails a commit with no sign-off and names it' {
    $c = New-TestCommit -Message 'feat: a change'
    $v = Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -BeLike "aaaaaaaaaaaa feat: a change: no Signed-off-by*"
  }

  It 'fails a sign-off whose email is not the author''s' {
    $c = New-TestCommit -Message "feat: x`n`nSigned-off-by: Dana Doe <someone-else@example.com>"
    $v = Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -BeLike '*does not match the author*dana@example.com*'
  }

  It 'matches the email case-insensitively and accepts the trailer among others' {
    $c = New-TestCommit -Message "fix: y`n`nCo-Authored-By: Someone <x@example.com>`nsigned-off-by: Dana Doe <DANA@Example.com>"
    (Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted).Pass | Should -BeTrue
  }

  It 'does not accept the words in the body as a sign-off' {
    $c = New-TestCommit -Message "docs: explain`n`nCommits need a line like Signed-off-by: Dana Doe <dana@example.com> at the end."
    (Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted).Pass | Should -BeFalse
  }

  It 'skips a merge commit' {
    $c = New-TestCommit -Message 'Merge branch develop' -Parents 2
    $v = Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted
    $v.Pass | Should -BeTrue
    $v.Skipped | Should -Be 1
  }

  It 'skips a commit by a bot account (<email>)' -ForEach @(
      @{ name = 'dependabot[bot]'; email = '49699333+dependabot[bot]@users.noreply.github.com' }
      @{ name = 'github-actions[bot]'; email = '41898282+github-actions[bot]@users.noreply.github.com' }
  ) {
    $c = New-TestCommit -Message 'chore: bump' -AuthorName $name -AuthorEmail $email
    (Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted).Pass | Should -BeTrue
  }

  It 'skips a commit authored before the DCO was adopted' {
    $c = New-TestCommit -Message 'feat: old work' -Date '2026-10-03T23:59:59+00:00'
    $v = Get-DcoVerdict -Commits @($c) -AdoptedOn $Adopted
    $v.Pass | Should -BeTrue
    $v.Skipped | Should -Be 1
  }

  It 'checks every commit and reports each one that fails' {
    $good = New-TestCommit
    $bad1 = New-TestCommit -Message 'one'
    $bad2 = New-TestCommit -Message 'two'
    $v = Get-DcoVerdict -Commits @($good, $bad1, $bad2) -AdoptedOn $Adopted
    $v.Checked | Should -Be 3
    $v.Problems.Count | Should -Be 2
  }

  It 'fails rather than passes when there is nothing to check because the range read nothing' {
    $v = Get-DcoVerdict -Commits @() -AdoptedOn $Adopted
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -BeLike '*no commits*'
  }
}

Describe 'ConvertFrom-GitLog' {
  It 'parses the records git log writes, including multi-line bodies and merge parents' {
    $us = [char]0x1f; $rs = [char]0x1e
    $raw = "$('b' * 40)$us$('1' * 40) $('2' * 40)${us}Dana Doe${us}dana@example.com${us}2026-10-05T12:00:00+00:00${us}Merge x`n`nbody$rs`n" +
           "$('c' * 40)$us$('1' * 40)${us}Lee Roe${us}lee@example.com${us}2026-10-06T08:30:00-04:00${us}feat: y`n`nSigned-off-by: Lee Roe <lee@example.com>`n$rs"
    $commits = @(ConvertFrom-GitLog -Raw $raw)
    $commits.Count | Should -Be 2
    $commits[0].Parents | Should -Be 2
    $commits[1].Parents | Should -Be 1
    $commits[1].AuthorEmail | Should -Be 'lee@example.com'
    $commits[1].AuthorDate | Should -Be ([datetimeoffset]'2026-10-06T12:30:00Z')
    $commits[1].Message | Should -BeLike '*Signed-off-by: Lee Roe <lee@example.com>*'
  }
}
