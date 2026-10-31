# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The lock-file regeneration commits to Dependabot's branches with a GitHub App token, so what it decides
# (whether to run at all, what to restore, what to commit, as whom, and through which API calls) is tested
# here; git, gh and dotnet are mocked, and the restore targets and lock files are real files on TestDrive.

BeforeAll {
  # Pester mocks only a command that exists, and a runner may lack these tools (a Windows machine with no Docker
  # or GitHub CLI). A stand-in lets the mocks below attach; it throws if a test ever reaches it unmocked.
  foreach ($tool in 'gh', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
      Set-Item -Path "function:global:$tool" -Value ([scriptblock]::Create("throw '$tool is not installed'"))
    }
  }
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Update-DependabotLockFiles.ps1') -Step Credentials

  $script:AppEmail = '123456+wb-lockfiles[bot]@users.noreply.github.com'
  $script:HeadSha = 'a' * 40

  # Lays out a repository: a solution with one project, plus one locked project outside it.
  function New-TestRepo {
    $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
    foreach ($dir in 'src/In', 'tests/Out', 'tools/NoProject') { New-Item -ItemType Directory -Path (Join-Path $root $dir) -Force | Out-Null }
    Set-Content -Path (Join-Path $root 'W.slnx') -Value '<Solution><Folder Name="/src/"><Project Path="src/In/In.csproj" /></Folder></Solution>'
    Set-Content -Path (Join-Path $root 'src/In/In.csproj') -Value '<Project />'
    Set-Content -Path (Join-Path $root 'tests/Out/Out.csproj') -Value '<Project />'
    Set-Content -Path (Join-Path $root 'src/In/packages.lock.json') -Value '{"in":1}' -NoNewline
    Set-Content -Path (Join-Path $root 'tests/Out/packages.lock.json') -Value '{"out":2}' -NoNewline
    return $root
  }

  # The git mock reads its answers from $script:GitState; each test sets what differs.
  function Reset-GitState {
    $script:GitState = @{
      LsFiles = @('src/In/packages.lock.json', 'tests/Out/packages.lock.json', 'tools/NoProject/packages.lock.json')
      HeadAuthor = "dependabot[bot]`t49699333+dependabot[bot]@users.noreply.github.com"
      Status = @(' M src/In/packages.lock.json', ' M tests/Out/packages.lock.json')
      FailOn = ''
    }
  }

  # The gh mock records every call ($script:GhCalls, the JSON body parsed) and answers from $script:GhState.
  function Reset-GhState {
    $script:GhCalls = [System.Collections.Generic.List[object]]::new()
    $script:GhState = @{ RefExit = 0; RemoteHead = $script:HeadSha; RemoteExit = 0; FailOn = '' }
  }

  function Get-GhCall {
    param([string]$Like)
    @($script:GhCalls | Where-Object { $_.Path -like $Like })
  }

  function Invoke-Regenerate {
    Invoke-Step -Step Regenerate -AppSlug 'wb-lockfiles' -HeadRef 'dependabot/nuget/x' -HeadSha $script:HeadSha `
        -Repository 'o/r' -Solution 'W.slnx' -Token 'tkn'
  }
}

Describe 'Get-CredentialVerdict' {
  It 'is ready when both secrets are set' {
    (Get-CredentialVerdict -HasAppId 'true' -HasPrivateKey 'true').Ready | Should -BeTrue
  }

  It 'is not ready and names <missing> when it is not set' -ForEach @(
      @{ id = 'false'; key = 'true'; missing = 'LOCKFILE_APP_ID' }
      @{ id = 'true'; key = ''; missing = 'LOCKFILE_APP_PRIVATE_KEY' }
      @{ id = ''; key = 'false'; missing = 'LOCKFILE_APP_ID and LOCKFILE_APP_PRIVATE_KEY' }) {
    $v = Get-CredentialVerdict -HasAppId $id -HasPrivateKey $key
    $v.Ready | Should -BeFalse
    $v.Message | Should -BeLike "*secret(s) $missing are not set*WORKFLOWS.md*"
  }
}

Describe 'Get-BotIdentity' {
  It 'is the app''s [bot] account and its users.noreply address' {
    $i = Get-BotIdentity -Slug 'wb-lockfiles' -UserId '123456'
    $i.Name | Should -Be 'wb-lockfiles[bot]'
    $i.Email | Should -Be $AppEmail
  }
}

Describe 'Get-SolutionProject' {
  It 'lists every project at any depth, with forward slashes' {
    $xml = '<Solution><Project Path="a/A.csproj" /><Folder Name="/t/"><Project Path="t\B\B.csproj" /></Folder></Solution>'
    Get-SolutionProject -SlnxXml $xml | Should -Be @('a/A.csproj', 't/B/B.csproj')
  }

  It 'reads the repository''s own solution' {
    $slnx = Join-Path $PSScriptRoot '..' '..' '..' 'Whizbang.slnx'
    $projects = Get-SolutionProject -SlnxXml (Get-Content -Raw $slnx)
    $projects | Should -Contain 'src/Whizbang.Core/Whizbang.Core.csproj'
    $projects | Should -Not -Contain 'tests/Whizbang.Soak.Tests/Whizbang.Soak.Tests.csproj'
  }
}

Describe 'Get-RestoreTarget' {
  It 'restores the solution first, then each locked project outside it, once' {
    $t = Get-RestoreTarget -Solution 'W.slnx' -SolutionProjects @('src/A/A.csproj') `
        -LockedProjects @('src\A\A.csproj', 'tests/Z/Z.csproj', 'bench/B/B.csproj', 'tests/Z/Z.csproj')
    $t | Should -Be @('W.slnx', 'bench/B/B.csproj', 'tests/Z/Z.csproj')
  }

  It 'is the solution alone when every locked project is in it' {
    Get-RestoreTarget -Solution 'W.slnx' -SolutionProjects @('SRC/A/A.csproj') -LockedProjects @('src/A/A.csproj') | Should -Be @('W.slnx')
  }

  It 'covers the two projects this repository keeps outside the solution' {
    $root = Join-Path $PSScriptRoot '..' '..' '..'
    Push-Location $root
    try {
      $t = Get-RestoreTarget -Solution 'Whizbang.slnx' -SolutionProjects (Get-SolutionProject -SlnxXml (Get-Content -Raw 'Whizbang.slnx')) `
          -LockedProjects @(Get-LockedProject)
    }
    finally { Pop-Location }
    $t[0] | Should -Be 'Whizbang.slnx'
    $t | Should -Contain 'benchmarks/Whizbang.Benchmarks.Postgres/Whizbang.Benchmarks.Postgres.csproj'
    $t | Should -Contain 'tests/Whizbang.Soak.Tests/Whizbang.Soak.Tests.csproj'
  }
}

Describe 'Get-ChangedFile' {
  It 'separates lock files from anything else' {
    $c = Get-ChangedFile -Porcelain @(' M src/A/packages.lock.json', '?? tests/New/packages.lock.json', ' M Directory.Packages.props',
        'R  old/x.json -> "new dir/packages.lock.json"', '', 'M')
    $c.LockFiles | Should -Be @('src/A/packages.lock.json', 'tests/New/packages.lock.json', 'new dir/packages.lock.json')
    $c.Other | Should -Be @('Directory.Packages.props')
  }

  It 'is empty for a clean tree' {
    $c = Get-ChangedFile -Porcelain @()
    $c.LockFiles.Count | Should -Be 0
    $c.Other.Count | Should -Be 0
  }
}

Describe 'Get-CommitMessage' {
  It 'is a conventional chore with the count and the reason, signed off by the app' {
    $m = Get-CommitMessage -Count 14 -Identity (Get-BotIdentity -Slug 'wb-lockfiles' -UserId '123456')
    ($m -split "`n")[0] | Should -Be 'chore(deps): regenerate 14 NuGet lock file(s) for the Dependabot update'
    $m | Should -BeLike '*NU1004*--force-evaluate*'
    ($m.TrimEnd() -split "`n")[-1] | Should -Be "Signed-off-by: wb-lockfiles[bot] <$AppEmail>"
  }
}

Describe 'Test-AppCommit' {
  BeforeAll { $script:Identity = Get-BotIdentity -Slug 'wb-lockfiles' -UserId '123456' }

  It 'is <expected> for "<value>"' -ForEach @(
      @{ value = "wb-lockfiles[bot]`tx@example.com"; expected = $true }
      @{ value = "someone`t123456+WB-LOCKFILES[bot]@users.noreply.github.com"; expected = $true }
      @{ value = "dependabot[bot]`t49699333+dependabot[bot]@users.noreply.github.com"; expected = $false }
      @{ value = ''; expected = $false }
      @{ value = 'wb-lockfiles[bot]'; expected = $true }) {
    Test-AppCommit -NameAndEmail $value -Identity $Identity | Should -Be $expected
  }
}

Describe 'Invoke-Step Credentials' {
  BeforeEach {
    Mock Write-Host {}
    $env:GITHUB_OUTPUT = Join-Path $TestDrive 'out.txt'
    $env:GITHUB_STEP_SUMMARY = Join-Path $TestDrive 'summary.md'
    Remove-Item $env:GITHUB_OUTPUT, $env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue
  }
  AfterEach { Remove-Item Env:GITHUB_OUTPUT, Env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue }

  It 'without the secrets, outputs ready=false with a notice and exits 0' {
    Invoke-Step -Step Credentials -HasAppId 'false' -HasPrivateKey 'false' | Should -Be 0
    Get-Content $env:GITHUB_OUTPUT | Should -Be 'ready=false'
    Should -Invoke Write-Host -ParameterFilter { $Object -like '::notice title=Dependabot lock files::*not set*' }
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike '*LOCKFILE_APP_ID*'
  }

  It 'with the secrets, outputs ready=true' {
    Invoke-Step -Step Credentials -HasAppId 'true' -HasPrivateKey 'true' | Should -Be 0
    Get-Content $env:GITHUB_OUTPUT | Should -Be 'ready=true'
    Should -Invoke Write-Host -Times 0 -ParameterFilter { $Object -like '::notice*' }
  }

  It 'writes nothing when run outside Actions' {
    Remove-Item Env:GITHUB_OUTPUT, Env:GITHUB_STEP_SUMMARY
    Invoke-Step -Step Credentials -HasAppId '' -HasPrivateKey '' | Should -Be 0
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*not set*' }
  }
}

Describe 'Invoke-Step Regenerate' {
  BeforeEach {
    Reset-GitState
    Reset-GhState
    $script:Repo = New-TestRepo
    Push-Location $script:Repo
    $env:GITHUB_STEP_SUMMARY = Join-Path $script:Repo 'summary.md'

    Mock Write-Host {}
    Mock dotnet { $global:LASTEXITCODE = 0 }
    Mock gh {
      $a = @($args | ForEach-Object { $_ })
      $path = @($a | Where-Object { $_ -like 'users/*' -or $_ -like 'repos/*' })[0]
      $at = [array]::IndexOf($a, '--method')
      $method = if ($at -ge 0) { $a[$at + 1] } else { 'GET' }
      $at = [array]::IndexOf($a, '--input')
      $inFile = if ($at -ge 0) { $a[$at + 1] } else { $null }
      $body = if ($inFile) { Get-Content -Raw -LiteralPath $inFile | ConvertFrom-Json } else { $null }
      $script:GhCalls.Add([pscustomobject]@{ Method = $method; Path = $path; Body = $body; Input = $inFile })
      $global:LASTEXITCODE = if ($script:GhState.FailOn -and $path -like "*$($script:GhState.FailOn)") { 1 } else { 0 }
      if ($global:LASTEXITCODE -ne 0) { return }
      switch -Wildcard ($path) {
        'users/*' { '123456' }
        '*/git/blobs' { '{"sha":"blob-' + $script:GhCalls.Count + '"}' }
        '*/git/trees' { '{"sha":"tree-new"}' }
        '*/git/commits' { '{"sha":"' + ('c' * 40) + '"}' }
        '*/git/refs/heads/*' {
          $global:LASTEXITCODE = $script:GhState.RefExit
          if ($script:GhState.RefExit -eq 0) { '{"object":{"sha":"' + ('c' * 40) + '"}}' }
        }
        '*/git/ref/heads/*' { $global:LASTEXITCODE = $script:GhState.RemoteExit; $script:GhState.RemoteHead }
      }
    }
    Mock git {
      $verb = @($args | Where-Object { $_ -in 'ls-files', 'log', 'status', 'rev-parse' })[0]
      $global:LASTEXITCODE = if ($verb -eq $script:GitState.FailOn) { 128 } else { 0 }
      switch ($verb) {
        'ls-files' { $script:GitState.LsFiles }
        'log' { $script:GitState.HeadAuthor }
        'status' { $script:GitState.Status }
        'rev-parse' { 'tree-base' }
      }
    }
  }
  AfterEach {
    Pop-Location
    Remove-Item Env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue
  }

  It 'restores the solution and the project outside it, and commits the lock files through the API as the app' {
    Invoke-Regenerate | Should -Be 0

    Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'W.slnx' -and $args[2] -eq '--force-evaluate' }
    Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'tests/Out/Out.csproj' -and $args[2] -eq '--force-evaluate' }
    Should -Invoke dotnet -Times 2 -Exactly
    Should -Invoke git -Times 1 -Exactly -ParameterFilter { $args[0] -eq 'rev-parse' -and $args[1] -eq "$('a' * 40)^{tree}" }

    @($script:GhCalls | ForEach-Object { "$($_.Method) $($_.Path)" }) | Should -Be @(
        'GET users/wb-lockfiles%5Bbot%5D'
        'POST repos/o/r/git/blobs'
        'POST repos/o/r/git/blobs'
        'POST repos/o/r/git/trees'
        'POST repos/o/r/git/commits'
        'PATCH repos/o/r/git/refs/heads/dependabot/nuget/x')

    $blobs = Get-GhCall -Like '*/git/blobs'
    $blobs.Body.encoding | Should -Be @('base64', 'base64')
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blobs[0].Body.content)) | Should -Be '{"in":1}'
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blobs[1].Body.content)) | Should -Be '{"out":2}'

    $tree = (Get-GhCall -Like '*/git/trees')[0].Body
    $tree.base_tree | Should -Be 'tree-base'
    $tree.tree.path | Should -Be @('src/In/packages.lock.json', 'tests/Out/packages.lock.json')
    $tree.tree.sha | Should -Be @('blob-2', 'blob-3')
    $tree.tree.mode | Should -Be @('100644', '100644')
    $tree.tree.type | Should -Be @('blob', 'blob')
  }

  It 'gives the commit no author or committer, so GitHub records the app and signs it, and signs it off' {
    Invoke-Regenerate | Should -Be 0
    $commit = (Get-GhCall -Like '*/git/commits')[0].Body
    @($commit.PSObject.Properties.Name) | Should -Be @('message', 'tree', 'parents')
    $commit.tree | Should -Be 'tree-new'
    @($commit.parents) | Should -Be @($script:HeadSha)
    ($commit.message -split "`n")[0] | Should -Be 'chore(deps): regenerate 2 NuGet lock file(s) for the Dependabot update'
    $lines = $commit.message.TrimEnd() -split "`n"
    $lines[-1] | Should -Be "Signed-off-by: wb-lockfiles[bot] <$AppEmail>"
    $lines[-2] | Should -BeNullOrEmpty
  }

  It 'moves the branch only by fast-forward, to the new commit' {
    Invoke-Regenerate | Should -Be 0
    $ref = (Get-GhCall -Like '*/git/refs/heads/*')[0].Body
    $ref.sha | Should -Be ('c' * 40)
    $ref.force | Should -BeFalse
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike "*Committed 2 regenerated lock file(s) to dependabot/nuget/x as wb-lockfiles``[bot``] ($('c' * 40), signed by GitHub)*tests/Out/packages.lock.json*"
  }

  It 'removes a lock file the restore deleted from the tree' {
    $script:GitState.Status = @(' M src/In/packages.lock.json', ' D gone/packages.lock.json')
    Invoke-Regenerate | Should -Be 0
    @(Get-GhCall -Like '*/git/blobs').Count | Should -Be 1
    $entries = @((Get-GhCall -Like '*/git/trees')[0].Body.tree)
    $entries[1].path | Should -Be 'gone/packages.lock.json'
    $entries[1].sha | Should -BeNullOrEmpty
    $entries[1].PSObject.Properties.Name | Should -Contain 'sha'
  }

  It 'commits nothing when every lock file is already current (a re-run is a no-op)' {
    $script:GitState.Status = @()
    Invoke-Regenerate | Should -Be 0
    @(Get-GhCall -Like 'repos/*').Count | Should -Be 0
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike '*already matches the restore of 2 target(s)*'
  }

  It 'warns about, and never commits, a change that is not a lock file' {
    $script:GitState.Status = @(' M src/In/packages.lock.json', ' M global.json')
    Invoke-Regenerate | Should -Be 0
    Should -Invoke Write-Host -ParameterFilter { $Object -like '::warning*global.json*not committed*' }
    @((Get-GhCall -Like '*/git/trees')[0].Body.tree.path) | Should -Be @('src/In/packages.lock.json')
  }

  It 'does nothing when the head commit is already the app''s, by <case>' -ForEach @(
      @{ case = 'address'; author = "someone`t$('123456+wb-lockfiles[bot]@users.noreply.github.com'.ToUpperInvariant())" }
      @{ case = 'name'; author = "wb-lockfiles[bot]`tother@example.com" }) {
    $script:GitState.HeadAuthor = $author
    Invoke-Regenerate | Should -Be 0
    Should -Invoke dotnet -Times 0
    @(Get-GhCall -Like 'repos/*').Count | Should -Be 0
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike "*already wb-lockfiles``[bot``]'s lock-file commit*"
  }

  It 'is a notice, not a failure, when the branch moved while it ran' {
    $script:GhState.RefExit = 1
    $script:GhState.RemoteHead = 'b' * 40
    Invoke-Regenerate | Should -Be 0
    Should -Invoke Write-Host -ParameterFilter { $Object -like "::notice*moved to $('b' * 40)*" }
  }

  It 'fails when the branch update is refused and the branch did not move' {
    $script:GhState.RefExit = 1
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -eq "::error title=Dependabot lock files::Updating dependabot/nuget/x to $('c' * 40) failed" }
  }

  It 'fails when the branch cannot be read after a refused update' {
    $script:GhState.RefExit = 1
    $script:GhState.RemoteExit = 1
    Invoke-Regenerate | Should -Be 1
  }

  It 'fails, and moves no branch, when <what> fails' -ForEach @(
      @{ suffix = 'git/blobs'; what = 'Uploading src/In/packages.lock.json' }
      @{ suffix = 'git/trees'; what = 'Creating the tree' }
      @{ suffix = 'git/commits'; what = 'Creating the commit' }) {
    $script:GhState.FailOn = $suffix
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like "*$what failed (exit 1)*" }
    @(Get-GhCall -Like '*/git/refs/*').Count | Should -Be 0
  }

  It 'deletes every request body file it wrote, after a failed call too' {
    $script:GhState.RefExit = 1
    Invoke-Regenerate | Should -Be 1
    $inputs = @($script:GhCalls | Where-Object { $_.Input } | ForEach-Object Input)
    $inputs.Count | Should -Be 5
    $inputs | Where-Object { Test-Path -LiteralPath $_ } | Should -BeNullOrEmpty
  }

  It 'fails when a restore fails, and says which' {
    Mock dotnet { $global:LASTEXITCODE = 1 }
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*dotnet restore W.slnx failed (exit 1)*' }
    Should -Invoke Write-Host -ParameterFilter { $Object -eq '::endgroup::' }
    @(Get-GhCall -Like 'repos/*').Count | Should -Be 0
  }

  It 'fails when the app''s user cannot be looked up' {
    $script:GhState.FailOn = 'users/wb-lockfiles%5Bbot%5D'
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*Looking up the user id of wb-lockfiles`[bot`] failed*' }
  }

  It 'fails when git <verb> fails' -ForEach @(@{ verb = 'ls-files' }, @{ verb = 'log' }, @{ verb = 'status' }, @{ verb = 'rev-parse' }) {
    $script:GitState.FailOn = $verb
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like "*git $verb failed (exit 128)*" }
  }

  It 'fails without a token' {
    Invoke-Step -Step Regenerate -AppSlug 'wb-lockfiles' -HeadRef 'h' -HeadSha 's' -Repository 'o/r' -Solution 'W.slnx' -Token '' | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*LOCKFILE_TOKEN is not set*' }
    $script:GhCalls.Count | Should -Be 0
  }

  It 'fails without the pull request''s head branch' {
    Invoke-Step -Step Regenerate -AppSlug 'wb-lockfiles' -HeadRef '' -HeadSha 's' -Repository 'o/r' -Solution 'W.slnx' -Token 't' | Should -Be 1
    $script:GhCalls.Count | Should -Be 0
  }
}

Describe 'Get-LockedProject' {
  It 'maps a lock file at the repository root to the root project' {
    $root = Join-Path $TestDrive 'rootproj'
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Set-Content -Path (Join-Path $root 'Root.csproj') -Value '<Project />'
    Mock git { $global:LASTEXITCODE = 0; 'packages.lock.json' }
    Push-Location $root
    try { Get-LockedProject | Should -Be 'Root.csproj' }
    finally { Pop-Location }
  }
}

Describe 'the script run as a process' {
  It 'skips with a notice and exits 0 when the secrets are missing' {
    $script = Join-Path $PSScriptRoot '..' 'Update-DependabotLockFiles.ps1'
    $out = & pwsh -NoProfile -File $script -Step Credentials -HasAppId false -HasPrivateKey false *>&1
    $LASTEXITCODE | Should -Be 0
    ($out -join "`n") | Should -BeLike '*::notice title=Dependabot lock files::*not set*'
  }

  It 'fails with an error and exits 1 when regeneration has no token' {
    $script = Join-Path $PSScriptRoot '..' 'Update-DependabotLockFiles.ps1'
    $saved = $env:LOCKFILE_TOKEN
    $env:LOCKFILE_TOKEN = ''
    try { $out = & pwsh -NoProfile -File $script -Step Regenerate *>&1 }
    finally { $env:LOCKFILE_TOKEN = $saved }
    $LASTEXITCODE | Should -Be 1
    ($out -join "`n") | Should -BeLike '*::error*LOCKFILE_TOKEN is not set*'
  }
}
