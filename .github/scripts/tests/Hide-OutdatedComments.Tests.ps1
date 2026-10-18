#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Hide-OutdatedComments.ps1') -Repository o/r -PullRequest 1 -Marker m

  function New-Comment([string]$NodeId, [string]$Body, [string]$Login = 'github-actions[bot]', [bool]$Minimized = $false) {
    [pscustomobject]@{ NodeId = $NodeId; Body = $Body; Login = $Login; IsMinimized = $Minimized }
  }
}

Describe 'Select-OutdatedComment' {
  It 'picks every earlier bot comment with the marker and keeps the latest' {
    $comments = @(
      (New-Comment 'n1' '<!-- pr-review-checks -->one'),
      (New-Comment 'n2' '<!-- pr-quality-gate -->other source'),
      (New-Comment 'n3' '<!-- pr-review-checks -->two'),
      (New-Comment 'n4' '<!-- pr-review-checks -->three, the latest')
    )
    @(Select-OutdatedComment -Comments $comments -Marker '<!-- pr-review-checks -->') | Should -Be @('n1', 'n3')
  }

  It 'never picks a comment someone else posted, or one already minimized' {
    $comments = @(
      (New-Comment 'n1' '<!-- pr-review-checks -->quoted by a person' -Login 'dana'),
      (New-Comment 'n2' '<!-- pr-review-checks -->hidden already' -Minimized $true),
      (New-Comment 'n3' '<!-- pr-review-checks -->latest')
    )
    @(Select-OutdatedComment -Comments $comments -Marker '<!-- pr-review-checks -->').Count | Should -Be 0
  }

  It 'picks nothing when there is one comment' {
    @(Select-OutdatedComment -Comments @(New-Comment 'n1' '<!-- pr-review-checks -->only') -Marker '<!-- pr-review-checks -->').Count | Should -Be 0
  }
}
