# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../Test-DocsTagTarget.ps1'
  . $script:ScriptPath
  $script:RepoRoot = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve

  # A fake directory tree in $TestDrive: keys are relative paths, values are file contents.
  function New-FakeTree([hashtable]$Files) {
    $root = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    foreach ($relative in $Files.Keys) {
      $path = Join-Path -Path $root -ChildPath $relative
      New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null
      Set-Content -Path $path -Value $Files[$relative] -NoNewline
    }
    return $root
  }

  # A fake docs repository: keys are paths under src/assets/docs.
  function New-FakeDocs([hashtable]$Pages) {
    $files = @{}
    foreach ($page in $Pages.Keys) { $files["src/assets/docs/$page"] = $Pages[$page] }
    return New-FakeTree $files
  }

  $script:Page = @'
---
title: Workers
# a: yaml comment
---
# Perspective Worker

## Claiming work {#claim}

## Retry & Backoff

```csharp
## Not A Heading In Code
```

~~~
## Not A Heading Either
```
## Still Inside The Tilde Fence
~~~

## Retry & Backoff

### `IWorker.RunAsync()` method
'@

  $script:Docs = New-FakeDocs @{
    'v1.0.0/operations/workers/perspective-worker.md' = $script:Page
    'v1.0.0/fundamentals/dispatcher/_folder.md'       = '# Dispatcher'
    'v1.0.0/fundamentals/events/events.md'            = '# Events'
    'contributors/repository-checks.md'               = '# Repository checks'
    'drafts/fundamentals/draft-only.md'               = '# Draft'
  }

  function Resolve-Text([string]$Text) {
    return Resolve-DocsTagTarget -Target $Text -Site (Read-DocsSite -DocsRoot $script:Docs)
  }
}

Describe 'Get-DocsTagInText' {
  It 'finds tags with their line numbers and trims the text' {
    $tags = @(Get-DocsTagInText -Text "one`n/// <docs>  a/b#c  </docs>`r`n-- <docs>d/e</docs> <docs>f</docs>" -Source 'src/X.cs')
    @($tags | ForEach-Object { "$($_.Source):$($_.Line):$($_.Text):$($_.Closed)" }) | Should -Be @('src/X.cs:2:a/b#c:True', 'src/X.cs:3:d/e:True', 'src/X.cs:3:f:True')
  }

  It 'counts only tags after a comment marker on their line: in a comment, or in a string that emits one' {
    $text = @(
      '/// <docs>doc/comment</docs>',
      '    // <docs>line/comment</docs>',
      '    sb.AppendLine("/// <docs>generated/comment</docs>");',
      '-- <docs>sql/comment</docs>',
      '  [GeneratedRegex("<docs>([^<]+)</docs>")]',
      '  dangling.Add($"{name}: <docs>{tag}</docs>");'
    ) -join "`n"
    @(Get-DocsTagInText -Text $text -Source 'src/X.cs' | ForEach-Object { "$($_.Line):$($_.Text)" }) |
      Should -Be @('1:doc/comment', '2:line/comment', '3:generated/comment', '4:sql/comment')
  }

  It 'reports a tag that does not close on its own line as unterminated' {
    $tags = @(Get-DocsTagInText -Text "/// <docs>a/b`n/// </docs>" -Source 'src/X.cs')
    $tags.Count | Should -Be 1
    $tags[0].Closed | Should -BeFalse
  }
}

Describe 'Get-DocsPageAnchor' {
  It 'derives the site''s heading ids: slugs, explicit {#id}, numbered duplicates, code blocks skipped' {
    $anchors = Get-DocsPageAnchor -Markdown $script:Page
    foreach ($id in 'perspective-worker', 'claim', 'retry-backoff', 'retry-backoff-1', 'iworkerrunasync-method') {
      $anchors.Contains($id) | Should -BeTrue -Because "the site renders a heading with id '$id'"
    }
    foreach ($id in 'claiming-work', 'not-a-heading-in-code', 'not-a-heading-either', 'still-inside-the-tilde-fence', 'Retry-Backoff') {
      $anchors.Contains($id) | Should -BeFalse -Because "the site renders no heading with id '$id'"
    }
  }

  It 'takes the id or name of an HTML element in the page, outside code blocks' {
    $anchors = Get-DocsPageAnchor -Markdown "<a id=""audit""></a>`n<span class=""x"" name='stream'></span>`n``````html`n<a id=""in-code""></a>`n``````"
    $anchors.Contains('audit') | Should -BeTrue
    $anchors.Contains('stream') | Should -BeTrue
    $anchors.Contains('in-code') | Should -BeFalse
  }

  It 'names a heading whose text has no word characters "header"' {
    (Get-DocsPageAnchor -Markdown '## !!!').Contains('header') | Should -BeTrue
  }
}

Describe 'Resolve-DocsTagTarget' {
  It 'resolves a present page and anchor: <Text>' -ForEach @(
    @{ Text = 'operations/workers/perspective-worker' },
    @{ Text = 'operations/workers/perspective-worker#retry-backoff' },
    @{ Text = 'operations/workers/perspective-worker#claim' },
    @{ Text = 'fundamentals/dispatcher' },
    @{ Text = 'fundamentals/events' },
    @{ Text = 'fundamentals/events/events#events' },
    @{ Text = 'contributors/repository-checks' },
    @{ Text = 'v1.0.0/fundamentals/events/events' }
  ) {
    Resolve-Text $Text | Should -BeNullOrEmpty
  }

  It 'reports a missing page: <Text>' -ForEach @(
    @{ Text = 'operations/workers/missing-worker' },
    @{ Text = 'operations/workers/missing-worker#claim' },
    @{ Text = 'Operations/workers/perspective-worker' },
    @{ Text = 'fundamentals/draft-only' }
  ) {
    Resolve-Text $Text | Should -Match '^missing page'
  }

  It 'reports a missing anchor on a page that exists: <Text>' -ForEach @(
    @{ Text = 'operations/workers/perspective-worker#claiming-work' },
    @{ Text = 'operations/workers/perspective-worker#Claim' },
    @{ Text = 'operations/workers/perspective-worker#not-a-heading-in-code' }
  ) {
    Resolve-Text $Text | Should -Match '^missing anchor'
  }

  It 'rejects <Text> as malformed' -ForEach @(
    @{ Text = '' }, @{ Text = '{tag}' }, @{ Text = 'a b' }, @{ Text = '/fundamentals/events' },
    @{ Text = 'fundamentals/../events' }, @{ Text = 'fundamentals//events' }, @{ Text = 'a#b#c' },
    @{ Text = 'fundamentals/events#' }, @{ Text = 'https://example.com/x' }
  ) {
    Resolve-Text $Text | Should -Match '^malformed'
  }

  It 'reads each page once' {
    $site = Read-DocsSite -DocsRoot $script:Docs
    Resolve-DocsTagTarget -Target 'fundamentals/events#events' -Site $site | Should -BeNullOrEmpty
    Remove-Item -Path (Join-Path -Path $script:Docs -ChildPath 'src/assets/docs/v1.0.0/fundamentals/events/events.md')
    Resolve-DocsTagTarget -Target 'fundamentals/events#events' -Site $site | Should -BeNullOrEmpty -Because 'the anchors are cached'
    Set-Content -Path (Join-Path -Path $script:Docs -ChildPath 'src/assets/docs/v1.0.0/fundamentals/events/events.md') -Value '# Events' -NoNewline
  }
}

Describe 'Read-DocsSite' {
  It 'refuses a docs tree with no pages, so an empty checkout cannot pass' {
    $empty = New-FakeTree @{ 'README.md' = 'x' }
    { Read-DocsSite -DocsRoot $empty } | Should -Throw -ExpectedMessage '*no pages*'
  }

  It 'tries the newest version before older ones' {
    $docs = New-FakeDocs @{ 'v1.0.0/a/b.md' = '# Old'; 'v10.0.0/a/b.md' = '# New'; 'v2.0.0/a/b.md' = '# Mid' }
    (Read-DocsSite -DocsRoot $docs).Versions | Should -Be @('v10.0.0', 'v2.0.0', 'v1.0.0')
    Resolve-DocsTagTarget -Target 'a/b#new' -Site (Read-DocsSite -DocsRoot $docs) | Should -BeNullOrEmpty
  }
}

Describe 'Find-DocsTag' {
  It 'scans .cs and .sql under src, tests, samples and tools, skipping bin, obj and other files' {
    $repo = New-FakeTree @{
      'src/P/A.cs'             = '/// <docs>a</docs>'
      'src/P/Migrations/1.sql' = "--`n-- <docs>b</docs>"
      'src/P/README.md'        = '<docs>c</docs>'
      'src/P/bin/G.cs'         = '/// <docs>d</docs>'
      'src/P/obj/G.cs'         = '/// <docs>e</docs>'
      'tests/T/T.cs'           = '/// <docs>f</docs>'
      'samples/S/S.cs'         = '/// <docs>g</docs>'
      'tools/U/U.cs'           = '/// <docs>h</docs>'
      'docs/D.cs'              = '/// <docs>i</docs>'
    }
    @(Find-DocsTag -Root $repo | Sort-Object Source | ForEach-Object { "$($_.Source):$($_.Line):$($_.Text)" }) |
      Should -Be @('samples/S/S.cs:1:g', 'src/P/A.cs:1:a', 'src/P/Migrations/1.sql:2:b', 'tests/T/T.cs:1:f', 'tools/U/U.cs:1:h')
  }
}

Describe 'Read-DocsTagBaseline' {
  It 'reads entries, skipping comments and blank lines' {
    $path = Join-Path -Path $TestDrive -ChildPath 'baseline-ok.txt'
    Set-Content -Path $path -Value @('# header', '', '  a/b#c | missing anchor | really  ', 'd/e|missing page')
    $baseline = Read-DocsTagBaseline -Path $path
    $baseline.Count | Should -Be 2
    $baseline['a/b#c'].Reason | Should -Be 'missing anchor | really'
    $baseline['a/b#c'].Line | Should -Be 3
  }

  It 'treats a missing baseline file as empty' {
    (Read-DocsTagBaseline -Path (Join-Path -Path $TestDrive -ChildPath 'none.txt')).Count | Should -Be 0
  }

  It 'refuses <Why>' -ForEach @(
    @{ Lines = @('a/b'); Why = 'an entry without a reason'; Message = '*target | reason*' },
    @{ Lines = @(' | missing'); Why = 'an empty target'; Message = '*target | reason*' },
    @{ Lines = @('a/b | x', 'a/b | y'); Why = 'a duplicate entry'; Message = '*duplicates line 1*' }
  ) {
    $path = Join-Path -Path $TestDrive -ChildPath "baseline-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content -Path $path -Value $Lines
    { Read-DocsTagBaseline -Path $path } | Should -Throw -ExpectedMessage $Message
  }
}

Describe 'Compare-DocsTagBaseline' {
  It 'reports unbaselined dangling targets and explains each stale entry, so the baseline only shrinks' {
    $tags = @(
      [pscustomobject]@{ Source = 'src/A.cs'; Line = 1; Text = 'new/page'; Closed = $true; Reason = 'missing page' },
      [pscustomobject]@{ Source = 'src/B.cs'; Line = 2; Text = 'known/page'; Closed = $true; Reason = 'missing page' },
      [pscustomobject]@{ Source = 'src/C.cs'; Line = 3; Text = 'known/page'; Closed = $true; Reason = 'missing page' },
      [pscustomobject]@{ Source = 'src/A.cs'; Line = 4; Text = 'fixed/page'; Closed = $true; Reason = $null }
    )
    $path = Join-Path -Path $TestDrive -ChildPath 'baseline-compare.txt'
    Set-Content -Path $path -Value @('known/page | missing page', 'fixed/page | missing page', 'gone/page | missing page')
    $comparison = Compare-DocsTagBaseline -Tags $tags -Baseline (Read-DocsTagBaseline -Path $path)
    @($comparison.Unbaselined | ForEach-Object Text) | Should -Be @('new/page')
    @($comparison.Stale | ForEach-Object { "$($_.Entry.Text): $($_.Resolved): $($_.Why)" }) | Should -Be @(
      'fixed/page: True: the target now resolves; delete the entry',
      'gone/page: False: no tag names that target any more; delete the entry'
    )
  }
}

Describe 'running the guard' {
  BeforeAll {
    $script:GuardDocs = New-FakeDocs @{ 'v1.0.0/a/page.md' = "# Page`n## Section" }
  }

  It 'passes when every target resolves' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = "/// <docs>a/page#section</docs>`n/// <docs>a/page</docs>" }
    $output = & $script:ScriptPath -Root $repo -DocsRoot $script:GuardDocs -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt')
    $LASTEXITCODE | Should -Be 0
    ($output -join "`n") | Should -Match 'Checked 2 <docs> tags \(2 distinct targets\): 0 dangling, 0 baselined, 0 stale baseline entries\.'
  }

  It 'fails on a new missing page or anchor, naming file and line, and tolerates baselined ones' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = "/// <docs>a/gone</docs>`n/// <docs>a/page#nope</docs>`n/// <docs>a/known</docs>`n/// <docs>a/gone</docs>`n/// <docs>a/open" }
    $baseline = Join-Path -Path $repo -ChildPath 'baseline.txt'
    Set-Content -Path $baseline -Value 'a/known | missing page'
    $output = & $script:ScriptPath -Root $repo -DocsRoot $script:GuardDocs -BaselinePath $baseline
    $LASTEXITCODE | Should -Be 1
    $text = $output -join "`n"
    $text | Should -Match 'src/P/A\.cs:1: <docs>a/gone</docs>: missing page'
    $text | Should -Match 'src/P/A\.cs:2: <docs>a/page#nope</docs>: missing anchor'
    $text | Should -Match 'src/P/A\.cs:4: <docs>a/gone</docs>: missing page'
    $text | Should -Match 'src/P/A\.cs:5: <docs>a/open</docs>: unterminated'
    $text | Should -Not -Match 'a/known</docs>:'
    $text | Should -Match 'Checked 5 <docs> tags \(4 distinct targets\): 4 dangling, 1 baselined, 0 stale'
  }

  It 'warns without failing on an entry whose target a docs change made resolve, since no W! pull request caused it' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = '/// <docs>a/page</docs>' }
    $baseline = Join-Path -Path $repo -ChildPath 'baseline.txt'
    Set-Content -Path $baseline -Value @('# known', 'a/page | missing page')
    $output = & $script:ScriptPath -Root $repo -DocsRoot $script:GuardDocs -BaselinePath $baseline
    $LASTEXITCODE | Should -Be 0
    $text = $output -join "`n"
    $text | Should -Match '::warning file=.*baseline\.txt,line=2::stale entry ''a/page'': the target now resolves'
    $text | Should -Match '1 stale baseline entries'
  }

  It 'fails on an entry no tag names any more, which only a change in this repository can cause' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = '/// <docs>a/page</docs>' }
    $baseline = Join-Path -Path $repo -ChildPath 'baseline.txt'
    Set-Content -Path $baseline -Value @('# known', 'a/unused | missing page')
    $output = & $script:ScriptPath -Root $repo -DocsRoot $script:GuardDocs -BaselinePath $baseline
    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match 'baseline\.txt:2: stale entry ''a/unused'': no tag names that target any more'
  }

  It 'fails when no tag is found, so a scan that finds nothing cannot pass' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = '// nothing' }
    $output = & $script:ScriptPath -Root $repo -DocsRoot $script:GuardDocs -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt')
    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match 'no <docs> tag'
  }

  It 'fetches the docs repository branch it is given and checks against it' {
    $origin = New-FakeTree @{ 'src/assets/docs/v1.0.0/a/page.md' = "# Page`n## Section" }
    git -C $origin init --quiet --initial-branch develop
    git -C $origin -c user.name=t -c user.email=t@example.com add -A
    git -C $origin -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false commit --quiet -m docs
    $repo = New-FakeTree @{ 'src/P/A.cs' = '/// <docs>a/page#section</docs>' }
    $output = & $script:ScriptPath -Root $repo -DocsRepository "file://$($origin.Replace('\', '/'))" -DocsRef develop -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt')
    $LASTEXITCODE | Should -Be 0 -Because ($output -join "`n")
  }

  It 'fails loudly, not green, when the docs checkout it is given holds no pages' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = '/// <docs>a/page</docs>' }
    $output = & $script:ScriptPath -Root $repo -DocsRoot (New-FakeTree @{ 'README.md' = 'x' }) -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt')
    $LASTEXITCODE | Should -Be 2
    ($output -join "`n") | Should -Match 'could not read the docs repository at .*no pages'
  }

  It 'fails loudly, not green, when the docs repository cannot be fetched' {
    $repo = New-FakeTree @{ 'src/P/A.cs' = '/// <docs>a/page</docs>' }
    $missing = "file://$((Join-Path -Path $TestDrive -ChildPath 'no-such-docs-repo').Replace('\', '/'))"
    $output = & $script:ScriptPath -Root $repo -DocsRepository $missing -DocsRef develop -BaselinePath (Join-Path -Path $repo -ChildPath 'none.txt') 2>&1
    $LASTEXITCODE | Should -Be 2
    ($output -join "`n") | Should -Match 'could not fetch the docs repository'
  }
}

Describe 'the docs tags in this repository' {
  It 'are found in their thousands, in both C# and SQL, and all name well-formed targets' {
    $tags = @(Find-DocsTag -Root $script:RepoRoot)
    $tags.Count | Should -BeGreaterThan 5000
    @($tags | Where-Object { $_.Source.EndsWith('.sql') }).Count | Should -BeGreaterThan 10
    $site = [pscustomobject]@{ Root = $TestDrive; Files = [System.Collections.Generic.HashSet[string]]::new(); Versions = @(); Anchors = @{} }
    @($tags | Where-Object { -not $_.Closed -or (Resolve-DocsTagTarget -Target $_.Text -Site $site) -match '^malformed' } |
      ForEach-Object { "$($_.Source):$($_.Line): $($_.Text)" }) | Should -BeNullOrEmpty
  }

  It 'has a baseline that parses and lists only well-formed targets' {
    $baseline = Read-DocsTagBaseline -Path (Join-Path -Path $script:RepoRoot -ChildPath '.github/scripts/docs-tag-target-baseline.txt')
    $baseline.Count | Should -BeGreaterThan 0
    foreach ($entry in $baseline.Values) { $entry.Text | Should -Match $script:TargetPattern }
  }
}
