#Requires -Modules Pester

# The lock-file regeneration pushes to Dependabot's branches with a GitHub App token, so what it decides
# (whether to run at all, what to restore, what to commit, as whom) is tested here; git, gh and dotnet are
# mocked, and the restore targets come from a real .slnx and project tree on TestDrive.

BeforeAll {
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
    return $root
  }

  # The git mock reads its answers from $script:GitState; each test sets what differs.
  function Reset-GitState {
    $script:GitState = @{
      LsFiles = @('src/In/packages.lock.json', 'tests/Out/packages.lock.json', 'tools/NoProject/packages.lock.json')
      HeadAuthor = '49699333+dependabot[bot]@users.noreply.github.com'
      Status = @(' M src/In/packages.lock.json', ' M tests/Out/packages.lock.json')
      PushExit = 0
      RemoteHead = $script:HeadSha
      FailOn = ''
    }
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
  It 'is a conventional chore with the count and the reason' {
    $m = Get-CommitMessage -Count 14
    ($m -split "`n")[0] | Should -Be 'chore(deps): regenerate 14 NuGet lock file(s) for the Dependabot update'
    $m | Should -BeLike '*NU1004*--force-evaluate*'
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
    $script:Repo = New-TestRepo
    Push-Location $script:Repo
    $env:GITHUB_STEP_SUMMARY = Join-Path $script:Repo 'summary.md'

    Mock Write-Host {}
    Mock gh { $global:LASTEXITCODE = 0; '123456' }
    Mock dotnet { $global:LASTEXITCODE = 0 }
    Mock git {
      $verb = @($args | Where-Object { $_ -in 'ls-files', 'log', 'status', 'add', 'commit', 'push', 'ls-remote' })[0]
      $global:LASTEXITCODE = if ($verb -eq $script:GitState.FailOn) { 128 } else { 0 }
      switch ($verb) {
        'ls-files' { $script:GitState.LsFiles }
        'log' { $script:GitState.HeadAuthor }
        'status' { $script:GitState.Status }
        'push' { $global:LASTEXITCODE = $script:GitState.PushExit }
        'ls-remote' { "$($script:GitState.RemoteHead)`trefs/heads/dependabot/nuget/x" }
      }
    }
  }
  AfterEach {
    Pop-Location
    Remove-Item Env:GITHUB_STEP_SUMMARY -ErrorAction SilentlyContinue
  }

  It 'restores the solution and the project outside it, commits the lock files signed off as the app, and pushes' {
    Invoke-Regenerate | Should -Be 0

    Should -Invoke gh -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'users/wb-lockfiles%5Bbot%5D' }
    Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'W.slnx' -and $args[2] -eq '--force-evaluate' }
    Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'tests/Out/Out.csproj' -and $args[2] -eq '--force-evaluate' }
    Should -Invoke dotnet -Times 2 -Exactly
    Should -Invoke git -Times 1 -Exactly -ParameterFilter {
      # A mocked native command sees `--` consumed and an array as one argument; git itself gets both.
      (@($args | ForEach-Object { $_ }) -join ',') -eq 'add,src/In/packages.lock.json,tests/Out/packages.lock.json'
    }
    Should -Invoke git -Times 1 -Exactly -ParameterFilter {
      $args[0] -eq '-c' -and $args[1] -eq 'user.name=wb-lockfiles[bot]' -and $args[2] -eq '-c' -and $args[3] -eq "user.email=$AppEmail" -and
      ($args[4..6] -join ' ') -eq 'commit --signoff --quiet' -and $args[8].StartsWith('chore(deps): regenerate 2 NuGet lock file(s)')
    }
    Should -Invoke git -Times 1 -Exactly -ParameterFilter {
      $args[0] -eq 'push' -and $args[2] -eq 'https://x-access-token:tkn@github.com/o/r.git' -and $args[3] -eq 'HEAD:refs/heads/dependabot/nuget/x'
    }
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike '*Pushed 2 regenerated lock file(s) to dependabot/nuget/x as wb-lockfiles`[bot`]*tests/Out/packages.lock.json*'
  }

  It 'commits nothing when every lock file is already current (a re-run is a no-op)' {
    $script:GitState.Status = @()
    Invoke-Regenerate | Should -Be 0
    Should -Invoke git -Times 0 -ParameterFilter { $args -contains 'commit' -or $args -contains 'push' -or $args -contains 'add' }
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike '*already matches the restore of 2 target(s)*'
  }

  It 'warns about, and never commits, a change that is not a lock file' {
    $script:GitState.Status = @(' M src/In/packages.lock.json', ' M global.json')
    Invoke-Regenerate | Should -Be 0
    Should -Invoke Write-Host -ParameterFilter { $Object -like '::warning*global.json*not committed*' }
    Should -Invoke git -Times 1 -Exactly -ParameterFilter { (@($args | ForEach-Object { $_ }) -join ',') -eq 'add,src/In/packages.lock.json' }
  }

  It 'does nothing when the head commit is already the app''s' {
    $script:GitState.HeadAuthor = $AppEmail.ToUpperInvariant()
    Invoke-Regenerate | Should -Be 0
    Should -Invoke dotnet -Times 0
    Should -Invoke git -Times 0 -ParameterFilter { $args -contains 'push' }
    Get-Content -Raw $env:GITHUB_STEP_SUMMARY | Should -BeLike "*already wb-lockfiles``[bot``]'s lock-file commit*"
  }

  It 'is a notice, not a failure, when the branch moved while it ran' {
    $script:GitState.PushExit = 1
    $script:GitState.RemoteHead = 'b' * 40
    Invoke-Regenerate | Should -Be 0
    Should -Invoke Write-Host -ParameterFilter { $Object -like "::notice*moved to $('b' * 40)*" }
  }

  It 'fails when the push is rejected and the branch did not move' {
    $script:GitState.PushExit = 1
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -eq '::error title=Dependabot lock files::git push to dependabot/nuget/x failed' }
  }

  It 'fails when the branch cannot be read after a rejected push' {
    $script:GitState.PushExit = 1
    $script:GitState.RemoteHead = ''
    Invoke-Regenerate | Should -Be 1
  }

  It 'fails when a restore fails, and says which' {
    Mock dotnet { $global:LASTEXITCODE = 1 }
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*dotnet restore W.slnx failed (exit 1)*' }
    Should -Invoke Write-Host -ParameterFilter { $Object -eq '::endgroup::' }
    Should -Invoke git -Times 0 -ParameterFilter { $args -contains 'commit' }
  }

  It 'fails when the app''s user cannot be looked up' {
    Mock gh { $global:LASTEXITCODE = 1 }
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*Looking up the user id of wb-lockfiles`[bot`] failed*' }
  }

  It 'fails when git <verb> fails' -ForEach @(@{ verb = 'ls-files' }, @{ verb = 'log' }, @{ verb = 'status' }, @{ verb = 'add' }, @{ verb = 'commit' }) {
    $script:GitState.FailOn = $verb
    Invoke-Regenerate | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like "*git $verb failed (exit 128)*" }
  }

  It 'fails without a token' {
    Invoke-Step -Step Regenerate -AppSlug 'wb-lockfiles' -HeadRef 'h' -HeadSha 's' -Repository 'o/r' -Solution 'W.slnx' -Token '' | Should -Be 1
    Should -Invoke Write-Host -ParameterFilter { $Object -like '*LOCKFILE_TOKEN is not set*' }
    Should -Invoke gh -Times 0
  }

  It 'fails without the pull request''s head branch' {
    Invoke-Step -Step Regenerate -AppSlug 'wb-lockfiles' -HeadRef '' -HeadSha 's' -Repository 'o/r' -Solution 'W.slnx' -Token 't' | Should -Be 1
    Should -Invoke gh -Times 0
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
