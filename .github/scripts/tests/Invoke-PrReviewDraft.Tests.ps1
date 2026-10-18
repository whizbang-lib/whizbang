#Requires -Modules Pester

BeforeAll {
  $script:Root = Join-Path $PSScriptRoot '..' '..' '..'
  Import-Module (Join-Path $Root 'scripts' 'lib' 'ReviewFindings.psm1') -Force
  . (Join-Path $Root 'scripts' 'Invoke-PrReviewDraft.ps1')

  $script:Head = 'abc1234def5678abc1234def5678abc1234def56'
  $script:Diff = @'
diff --git a/src/A.cs b/src/A.cs
index 1111111..2222222 100644
--- a/src/A.cs
+++ b/src/A.cs
@@ -10,2 +10,3 @@
 int a;
+int b;
 int c;
'@

  function New-BotComment([object[]]$Findings, [string]$Sha = $Head, [string]$Login = 'github-actions[bot]', [string]$Type = 'Bot', [string]$Source = 'review-checks') {
    [pscustomobject]@{ Login = $Login; Type = $Type; Body = "table`n" + (ConvertTo-ReviewBlock -Source $Source -Sha $Sha -Findings $Findings) }
  }
  $script:OnLine = New-ReviewFinding -Id 'style.en-us:src/A.cs:11' -Rule 'style.en-us' -Severity warning -Body 'Use "behavior".' -Path 'src/A.cs' -Line 11
  $script:OffDiff = New-ReviewFinding -Id 'coverage-line:src/A.cs:40' -Rule 'coverage.new-line' -Severity blocking -Body 'No test runs this line.' -Path 'src/A.cs' -Line 40
  $script:OnPr = New-ReviewFinding -Id 'dco:aaaaaaaaaaaa' -Rule 'dco.signoff' -Severity blocking -Body 'Commit aaaaaaaaaaaa has no sign-off.'
}

Describe 'Get-ReviewDraftPlan' {
  It 'puts a finding on its line when the line is in the diff' {
    $plan = Get-ReviewDraftPlan -Comments @(New-BotComment @($OnLine)) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count | Should -Be 1
    $plan.Threads[0].Path | Should -Be 'src/A.cs'
    $plan.Threads[0].Line | Should -Be 11
    $plan.Threads[0].Body | Should -BeLike 'Use "behavior".*<!-- wr:style.en-us:src/A.cs:11 -->'
    $plan.Summary.Count | Should -Be 0
  }

  It 'puts a finding outside the diff, or about the pull request, in the summary' {
    $plan = Get-ReviewDraftPlan -Comments @(New-BotComment @($OffDiff, $OnPr)) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count | Should -Be 0
    $plan.Summary.Body[0] | Should -Be '- `src/A.cs:40`: No test runs this line. <!-- wr:coverage-line:src/A.cs:40 -->'
    $plan.Summary.Body[1] | Should -Be '- Commit aaaaaaaaaaaa has no sign-off. <!-- wr:dco:aaaaaaaaaaaa -->'
  }

  It 'reads findings only from the workflow bot' -TestCases @(
    @{ Login = 'someone'; Type = 'User' }
    @{ Login = 'github-actions[bot]'; Type = 'User' }
    @{ Login = 'other-app[bot]'; Type = 'Bot' }
  ) {
    $plan = Get-ReviewDraftPlan -Comments @(New-BotComment @($OnLine) -Login $Login -Type $Type) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count + $plan.Summary.Count | Should -Be 0
  }

  It 'drafts only the latest run of a source, ignoring its runs on earlier commits' {
    # Every run keeps its comment, so a pull request carries one block per run; only the newest counts.
    $plan = Get-ReviewDraftPlan -Comments @((New-BotComment @($OffDiff) -Sha 'fff1234'), (New-BotComment @($OnLine))) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Id | Should -Be @('style.en-us:src/A.cs:11')
    $plan.Summary.Count | Should -Be 0
    $plan.Stale.Count | Should -Be 0
  }

  It 'drafts only the latest of two runs on the same commit' {
    $plan = Get-ReviewDraftPlan -Comments @((New-BotComment @($OnLine)), (New-BotComment @($OnPr))) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count | Should -Be 0
    $plan.Summary.Count | Should -Be 1
  }

  It 'reports a source as stale when its latest run checked another commit' {
    $plan = Get-ReviewDraftPlan -Comments @((New-BotComment @($OnLine)), (New-BotComment @($OnPr) -Sha 'fff1234')) -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count + $plan.Summary.Count | Should -Be 0
    @($plan.Stale).Count | Should -Be 1
    $plan.Stale[0].Sha | Should -Be 'fff1234'
  }

  It 'reports a block checked against another commit instead of drafting it' {
    $plan = Get-ReviewDraftPlan -Comments @(New-BotComment @($OnLine) -Sha 'fff1234') -HeadSha $Head -DiffText $Diff
    $plan.Threads.Count | Should -Be 0
    $plan.Stale[0].Sha | Should -Be 'fff1234'
  }

  It 'skips a finding already in the draft, and one two sources both report' {
    $plan = Get-ReviewDraftPlan -Comments @((New-BotComment @($OnLine, $OnPr)), (New-BotComment @($OnPr) -Source 'pr-quality-gate')) -HeadSha $Head -DiffText $Diff `
      -DraftedIds @('style.en-us:src/A.cs:11')
    $plan.Threads.Count | Should -Be 0
    $plan.Summary.Count | Should -Be 1
    $plan.AlreadyDrafted | Should -Be 2
  }
}

Describe 'Protect-ReviewText' {
  It 'removes HTML comment markers' {
    Protect-ReviewText 'a <!-- wr:fake --> b' | Should -Be 'a  wr:fake  b'
  }

  It 'keeps an @-mention from notifying anyone' {
    $t = Protect-ReviewText 'ask @octocat'
    $t | Should -Not -Match '@octocat'
    $t.Replace([string][char]0x200B, '') | Should -Be 'ask @octocat'
  }

  It 'bounds the length' {
    (Protect-ReviewText ('x' * 5000)).Length | Should -Be 3000
  }
}

Describe 'Get-DraftedIds' {
  It 'reads the finding ids marked in a draft' {
    @(Get-DraftedIds -Texts @('a <!-- wr:x:1 --> b', $null, 'c <!-- wr:dco:abc --> <!-- not:mine -->')) | Should -Be @('x:1', 'dco:abc')
  }
}

Describe 'Invoke-PrReviewDraft' {
  BeforeEach {
    $script:Mutations = [System.Collections.Generic.List[object]]::new()
    $script:Pending = $null
    $script:Comments = @(New-BotComment @($OnLine, $OnPr))

    Mock Get-PullRequestState {
      [pscustomobject]@{
        NodeId = 'PR_1'; HeadSha = $Head; Url = 'https://github.com/o/r/pull/7'
        Comments = $script:Comments; DiffText = $Diff; PendingReview = $script:Pending
      }
    }
    Mock Invoke-GraphQl {
      $script:Mutations.Add([pscustomobject]@{ Query = $Query; Variables = $Variables })
      [pscustomobject]@{ data = $null }
    }
  }

  It 'starts a pending review with the line comments and the summary when there is none' {
    Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 6>$null

    $Mutations.Count | Should -Be 1
    $Mutations[0].Query | Should -BeLike '*addPullRequestReview(*'
    $in = $Mutations[0].Variables.input
    $in.pullRequestId | Should -Be 'PR_1'
    $in.commitOID | Should -Be $Head
    $in.Keys | Should -Not -Contain 'event'
    $in.threads.Count | Should -Be 1
    $in.threads[0].path | Should -Be 'src/A.cs'
    $in.threads[0].line | Should -Be 11
    $in.threads[0].side | Should -Be 'RIGHT'
    $in.body | Should -BeLike '*<!-- wr:dco:aaaaaaaaaaaa -->*'
  }

  It 'adds to the pending review that already exists' {
    $script:Pending = [pscustomobject]@{ Id = 'PRR_9'; Body = 'my own notes'; Texts = @('my own notes') }
    Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 6>$null

    $Mutations.Query | Should -Not -BeLike '*addPullRequestReview(*'
    $thread = @($Mutations | Where-Object Query -Like '*addPullRequestReviewThread(*')
    $thread.Count | Should -Be 1
    $thread[0].Variables.input.pullRequestReviewId | Should -Be 'PRR_9'
    $thread[0].Variables.input.line | Should -Be 11
    $update = @($Mutations | Where-Object Query -Like '*updatePullRequestReview(*')
    $update.Count | Should -Be 1
    $update[0].Variables.input.body | Should -BeLike 'my own notes*<!-- wr:dco:aaaaaaaaaaaa -->*'
  }

  It 'prints the summary to paste at submit when the pending review was started without one' {
    # GitHub refuses to edit the body of a pending review that has none ("Could not edit a review with
    # a missing body"), which is how a review started in the browser begins.
    $script:Pending = [pscustomobject]@{ Id = 'PRR_9'; Body = ''; Texts = @() }
    $out = (Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 6>&1 | Out-String)

    @($Mutations | Where-Object Query -Like '*addPullRequestReviewThread(*').Count | Should -Be 1
    @($Mutations | Where-Object Query -Like '*updatePullRequestReview(*').Count | Should -Be 0
    $out | Should -Match 'paste'
    $out | Should -Match ([regex]::Escape('Commit aaaaaaaaaaaa has no sign-off.'))
    $out | Should -Match '1 summary item\(s\) to paste'
  }

  It 'changes nothing when every finding is already drafted' {
    $script:Pending = [pscustomobject]@{
      Id = 'PRR_9'; Body = '- x <!-- wr:dco:aaaaaaaaaaaa -->'
      Texts = @('- x <!-- wr:dco:aaaaaaaaaaaa -->', 'Use "behavior". <!-- wr:style.en-us:src/A.cs:11 -->')
    }
    Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 6>$null
    $Mutations.Count | Should -Be 0
  }

  It 'changes nothing under -WhatIf' {
    Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 -WhatIf 6>$null
    $Mutations.Count | Should -Be 0
  }

  It 'never submits the review' {
    $script:Pending = [pscustomobject]@{ Id = 'PRR_9'; Body = ''; Texts = @() }
    Invoke-PrReviewDraft -Repository 'o/r' -PullRequest 7 6>$null
    $Mutations.Query | Should -Not -BeLike '*submitPullRequestReview*'
  }
}

Describe 'Get-PullRequestState' {
  It 'reads the head, the bot comments, the diff and the viewer''s own pending review' {
    Mock Invoke-Gh {
      switch -Wildcard ($Arguments -join ' ') {
        'api repos/o/r/pulls/7 --jq*' { '{"head":"' + $Head + '","node":"PR_1","url":"https://github.com/o/r/pull/7"}' }
        'api --paginate repos/o/r/issues/7/comments*' { '{"login":"github-actions[bot]","type":"Bot","body":"b1"}', '{"login":"x","type":"User","body":"b2"}' }
        'api repos/o/r/pulls/7 -H*' { $Diff }
        default { throw "unexpected gh call: $($Arguments -join ' ')" }
      }
    }
    Mock Invoke-GraphQl {
      [pscustomobject]@{ data = [pscustomobject]@{ repository = [pscustomobject]@{ pullRequest = [pscustomobject]@{ reviews = [pscustomobject]@{ nodes = @(
        [pscustomobject]@{ id = 'PRR_other'; viewerDidAuthor = $false; body = 'theirs'; comments = [pscustomobject]@{ nodes = @() } }
        [pscustomobject]@{ id = 'PRR_mine'; viewerDidAuthor = $true; body = 'mine'; comments = [pscustomobject]@{ nodes = @([pscustomobject]@{ body = 'c1' }) } }
      ) } } } } }
    }

    $s = Get-PullRequestState -Repository 'o/r' -PullRequest 7

    $s.HeadSha | Should -Be $Head
    $s.NodeId | Should -Be 'PR_1'
    $s.Comments.Count | Should -Be 2
    $s.Comments[0].Login | Should -Be 'github-actions[bot]'
    $s.DiffText | Should -BeLike '*+int b;*'
    $s.PendingReview.Id | Should -Be 'PRR_mine'
    $s.PendingReview.Texts | Should -Be @('mine', 'c1')
  }
}
