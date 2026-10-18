#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Publish-SignedCommit.ps1') -Repository o/r -Branch b

  function Invoke-Git { & git @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" } }

  # A clone of a local bare repository with one published commit, then new local commits on top.
  function New-TestRepository {
    $root = Join-Path ([IO.Path]::GetTempPath()) ("psc-" + [guid]::NewGuid().ToString('N'))
    $bare = Join-Path $root 'remote.git'
    $work = Join-Path $root 'work'
    Invoke-Git init -q --bare -b develop $bare
    Invoke-Git clone -q $bare $work 2>$null
    Push-Location $work
    Invoke-Git config user.name t; Invoke-Git config user.email t@example.com; Invoke-Git config commit.gpgsign false
    Set-Content -Path a.txt -Value 'one' -NoNewline
    Set-Content -Path gone.txt -Value 'bye' -NoNewline
    Invoke-Git add -A; Invoke-Git commit -q -m base
    Invoke-Git push -q origin HEAD:develop 2>$null
    Pop-Location
    [pscustomobject]@{ Root = $root; Work = $work }
  }

  function Get-Tree([string]$Commit) { (git rev-parse "$Commit^{tree}").Trim() }
}

Describe 'ConvertFrom-DiffTree' {
  It 'reads the status and path of each change from NUL-separated output' {
    $raw = "M`0src/a.cs`0A`0new file.txt`0D`0old.txt`0"
    $c = @(ConvertFrom-DiffTree -Raw $raw)
    $c.Status | Should -Be @('M', 'A', 'D')
    $c.Path | Should -Be @('src/a.cs', 'new file.txt', 'old.txt')
  }

  It 'reads nothing from no changes' {
    @(ConvertFrom-DiffTree -Raw '').Count | Should -Be 0
  }
}

Describe 'Test-IsText' {
  It 'treats valid UTF-8 without NUL as text, anything else as binary' {
    Test-IsText -Bytes ([Text.Encoding]::UTF8.GetBytes("héllo`n")) | Should -BeTrue
    Test-IsText -Bytes ([byte[]](0x61, 0x00, 0x62)) | Should -BeFalse
    Test-IsText -Bytes ([byte[]](0xC3, 0x28)) | Should -BeFalse
  }
}

Describe 'Publish-SignedCommits' {
  BeforeEach {
    $script:repo = New-TestRepository
    Push-Location $repo.Work
    $script:calls = [System.Collections.Generic.List[object]]::new()
    $script:treeFor = @{}
    $script:refExists = $false
    $script:n = 0
    Mock Invoke-GitHubApi {
      $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
      switch -Wildcard ("$Method $Path") {
        'GET */git/ref/heads/*' { if ($script:refExists) { return [pscustomobject]@{ object = [pscustomobject]@{ sha = 'remotehead' } } } return $null }
        'POST */git/blobs' { return [pscustomobject]@{ sha = 'blob' + $script:calls.Count } }
        'POST */git/trees' { return [pscustomobject]@{ sha = $script:treeFor[$Body.base_tree + '|' + $script:calls.Count] ?? $script:nextTree } }
        'POST */git/commits' { $script:n++; return [pscustomobject]@{ sha = "new$($script:n)"; verification = [pscustomobject]@{ verified = $true } } }
        default { return [pscustomobject]@{ ok = $true } }
      }
    }
    Mock Sync-LocalBranch { }
  }

  AfterEach {
    Pop-Location
    Remove-Item -Recurse -Force $repo.Root
  }

  It 'recreates each unpublished commit through the API, oldest first, chaining the new parents' {
    $base = (git rev-parse HEAD).Trim()
    Set-Content a.txt 'two' -NoNewline; Remove-Item gone.txt; Invoke-Git add -A; Invoke-Git commit -q -m 'first change'
    $c1 = (git rev-parse HEAD).Trim()
    Set-Content b.txt 'three' -NoNewline; Invoke-Git add -A; Invoke-Git commit -q -m "second change`n`nbody"
    $c2 = (git rev-parse HEAD).Trim()
    # The API builds the same trees git did.
    $script:trees = @((Get-Tree $c1), (Get-Tree $c2))
    Mock Invoke-GitHubApi -ParameterFilter { $Method -eq 'POST' -and $Path -like '*/git/trees' } {
      $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
      $t = $script:trees[0]; $script:trees = @($script:trees | Select-Object -Skip 1)
      [pscustomobject]@{ sha = $t }
    }

    $result = Publish-SignedCommits -Repository 'o/r' -Branch 'feature'

    $commits = @($script:calls | Where-Object { $_.Path -like '*/git/commits' })
    $commits.Count | Should -Be 2
    $commits[0].Body.message | Should -Be 'first change'
    $commits[0].Body.parents | Should -Be @($base)
    $commits[1].Body.message | Should -Be "second change`n`nbody"
    $commits[1].Body.parents | Should -Be @('new1')
    $commits[0].Body.Keys | Should -Not -Contain 'author'
    $commits[0].Body.Keys | Should -Not -Contain 'committer'

    $trees = @($script:calls | Where-Object { $_.Path -like '*/git/trees' })
    $trees[0].Body.base_tree | Should -Be (Get-Tree $base)
    $entry = @($trees[0].Body.tree | Where-Object path -EQ 'a.txt')[0]
    $entry.content | Should -Be 'two'
    $entry.mode | Should -Be '100644'
    $gone = @($trees[0].Body.tree | Where-Object path -EQ 'gone.txt')[0]
    $gone.sha | Should -BeNullOrEmpty
    $gone.Keys | Should -Contain 'sha'

    $create = @($script:calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/git/refs' })
    $create[0].Body.ref | Should -Be 'refs/heads/feature'
    $create[0].Body.sha | Should -Be 'new2'
    $result | Should -Be 'new2'
    Should -Invoke Sync-LocalBranch -Times 1 -ParameterFilter { $Branch -eq 'feature' }
  }

  It 'recreates a merge commit with both parents' {
    $base = (git rev-parse HEAD).Trim()
    Invoke-Git checkout -q -b side
    Set-Content side.txt 'side' -NoNewline; Invoke-Git add -A; Invoke-Git commit -q -m side
    Invoke-Git push -q origin side 2>$null
    $side = (git rev-parse HEAD).Trim()
    Invoke-Git checkout -q develop
    Set-Content a.txt 'main' -NoNewline; Invoke-Git add -A; Invoke-Git commit -q -m mainline
    $c1 = (git rev-parse HEAD).Trim()
    Invoke-Git merge -q --no-edit -m 'merge side' side
    $merge = (git rev-parse HEAD).Trim()
    $script:trees = @((Get-Tree $c1), (Get-Tree $merge))
    Mock Invoke-GitHubApi -ParameterFilter { $Method -eq 'POST' -and $Path -like '*/git/trees' } {
      $t = $script:trees[0]; $script:trees = @($script:trees | Select-Object -Skip 1)
      [pscustomobject]@{ sha = $t }
    }

    Publish-SignedCommits -Repository 'o/r' -Branch 'release/v1' | Out-Null

    $commits = @($script:calls | Where-Object { $_.Path -like '*/git/commits' })
    $commits.Count | Should -Be 2
    $commits[1].Body.parents | Should -Be @('new1', $side)
  }

  It 'fails, publishing nothing, when the API builds a different tree than git did' {
    Set-Content a.txt 'two' -NoNewline; Invoke-Git add -A; Invoke-Git commit -q -m change
    $script:nextTree = 'not-the-same-tree'
    { Publish-SignedCommits -Repository 'o/r' -Branch 'feature' } | Should -Throw '*tree*'
    @($script:calls | Where-Object { $_.Path -like '*/git/commits' }).Count | Should -Be 0
  }

  It 'moves an existing branch, forced only when asked' {
    Set-Content a.txt 'two' -NoNewline; Invoke-Git add -A; Invoke-Git commit -q -m change
    $script:nextTree = Get-Tree HEAD
    $script:refExists = $true

    Publish-SignedCommits -Repository 'o/r' -Branch 'feature' | Out-Null
    $update = @($script:calls | Where-Object { $_.Method -eq 'PATCH' })
    $update[0].Path | Should -BeLike '*/git/refs/heads/feature'
    $update[0].Body.sha | Should -Be 'new1'
    $update[0].Body.force | Should -BeFalse

    $script:calls.Clear()
    Publish-SignedCommits -Repository 'o/r' -Branch 'feature' -Force | Out-Null
    @($script:calls | Where-Object { $_.Method -eq 'PATCH' })[0].Body.force | Should -BeTrue
  }

  It 'sends a binary file as a blob' {
    [IO.File]::WriteAllBytes((Join-Path $PWD 'bin.dat'), [byte[]](0, 1, 2, 255))
    Invoke-Git add -A; Invoke-Git commit -q -m binary
    $script:nextTree = Get-Tree HEAD

    Publish-SignedCommits -Repository 'o/r' -Branch 'feature' | Out-Null

    $blob = @($script:calls | Where-Object { $_.Path -like '*/git/blobs' })
    $blob.Count | Should -Be 1
    $blob[0].Body.encoding | Should -Be 'base64'
    $blob[0].Body.content | Should -Be ([Convert]::ToBase64String([byte[]](0, 1, 2, 255)))
    $entry = @(@($script:calls | Where-Object { $_.Path -like '*/git/trees' })[0].Body.tree | Where-Object path -EQ 'bin.dat')[0]
    $entry.sha | Should -BeLike 'blob*'
  }

  It 'changes nothing when the branch already points at a head the remote has' {
    $script:refExists = $true
    $head = (git rev-parse HEAD).Trim()
    Mock Invoke-GitHubApi -ParameterFilter { $Method -eq 'GET' } { [pscustomobject]@{ object = [pscustomobject]@{ sha = $head } } }
    Publish-SignedCommits -Repository 'o/r' -Branch 'develop' | Should -BeNullOrEmpty
    @($script:calls | Where-Object { $_.Method -ne 'GET' }).Count | Should -Be 0
  }

  It 'creates a missing branch at a head the remote already has, publishing no commit' {
    $head = (git rev-parse HEAD).Trim()
    Publish-SignedCommits -Repository 'o/r' -Branch 'sync/x' | Should -Be $head
    @($script:calls | Where-Object { $_.Path -like '*/git/commits' }).Count | Should -Be 0
    $create = @($script:calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/git/refs' })
    $create[0].Body.ref | Should -Be 'refs/heads/sync/x'
    $create[0].Body.sha | Should -Be $head
  }
}
