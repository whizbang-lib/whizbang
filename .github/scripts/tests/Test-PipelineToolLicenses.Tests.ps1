# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The pipeline's own tools are under licenses someone reviewed. A release was once canceled because the secret
# scanner action was AGPL-3.0 and dependency review refused it at the last step; these tests make the same
# mistake fail in the pull request that adds the tool, without any network access. The committed list,
# .github/pipeline-tool-licenses.txt, is the source of truth.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  . (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1')

  function New-TempDirectory {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('ptl-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $dir
  }

  # A minimal repository: one workflow, one composite action, one tool manifest.
  function New-FixtureRepository {
    param([string[]]$WorkflowLines, [string[]]$ListLines, [string]$Manifest)
    $root = New-TempDirectory
    New-Item -ItemType Directory -Path (Join-Path $root '.github' 'workflows'), (Join-Path $root '.github' 'actions' 'x'), (Join-Path $root '.config') | Out-Null
    Set-Content -LiteralPath (Join-Path $root '.github' 'workflows' 'w.yml') -Value $WorkflowLines
    Set-Content -LiteralPath (Join-Path $root '.github' 'actions' 'x' 'action.yml') -Value @('runs:', '  using: composite', '  steps:', '    - uses: owner/composite-tool@1234567 # v1')
    Set-Content -LiteralPath (Join-Path $root '.config' 'dotnet-tools.json') -Value $(if ($Manifest) { $Manifest } else { '{ "version": 1, "isRoot": true, "tools": { "Some.Tool": { "version": "1.0.0", "commands": [ "some" ] } } }' })
    Set-Content -LiteralPath (Join-Path $root '.github' 'pipeline-tool-licenses.txt') -Value $ListLines
    $root
  }

  $script:BaseWorkflow = @(
    'jobs:', '  a:', '    steps:',
    '      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1',
    '      - uses: ./.github/actions/x',
    '      - run: dotnet tool install --global trx2junit --version 2.1.0')
  $script:BaseList = @(
    'action       actions/checkout        MIT                # checkout',
    'action       owner/composite-tool    Apache-2.0         # used by the composite action',
    'dotnet-tool  some.tool               BSD-3-Clause       # from the manifest',
    'dotnet-tool  trx2junit               MIT                # installed by a workflow')
}

Describe 'Read-ToolLicenseList' {
  It 'reads every kind of tool the pipeline runs' {
    $dir = New-TempDirectory
    try {
      $path = Join-Path $dir 'l.txt'
      Set-Content -LiteralPath $path -Value @('action a/b MIT # x', 'dotnet-tool t MIT # y', 'release-binary c/d MIT # z', 'container e/f MIT # w')
      @(Read-ToolLicenseList -Path $path | ForEach-Object Kind) | Should -Be @('action', 'dotnet-tool', 'release-binary', 'container')
    } finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
  }

  It 'reads kind, id, license, the exception marker and the reason, skipping comments and blank lines' {
    $dir = New-TempDirectory
    try {
      $path = Join-Path $dir 'l.txt'
      Set-Content -LiteralPath $path -Value @(
        '# header', '',
        'action  Owner/Repo  MIT  # why',
        'dotnet-tool  dotnet-sonarscanner  LGPL-3.0-only  exception  # the only client')
      $entries = @(Read-ToolLicenseList -Path $path)
      $entries.Count | Should -Be 2
      $entries[0].Kind | Should -Be 'action'
      $entries[0].Id | Should -Be 'owner/repo'
      $entries[0].License | Should -Be 'MIT'
      $entries[0].Exception | Should -BeFalse
      $entries[0].Reason | Should -Be 'why'
      $entries[1].Exception | Should -BeTrue
      $entries[1].Reason | Should -Be 'the only client'
      $entries[1].Line | Should -Be 4
    } finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
  }

  It 'refuses a line that is not kind, id, license and an optional exception marker' -TestCases @(
    @{ Line = 'action  owner/repo  # no license' }
    @{ Line = 'action  owner/repo  MIT  extra  # unknown fourth column' }
    @{ Line = 'tarball  owner/repo  MIT  # unknown kind' }
  ) {
    param($Line)
    $dir = New-TempDirectory
    try {
      $path = Join-Path $dir 'l.txt'
      Set-Content -LiteralPath $path -Value @($Line)
      { Read-ToolLicenseList -Path $path } | Should -Throw '*l.txt:1*'
    } finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
  }
}

Describe 'Get-ActionRepository' {
  It 'reduces a uses: reference to the lowercase owner/repo its license belongs to' -TestCases @(
    @{ Reference = 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'; Expected = 'actions/checkout' }
    @{ Reference = 'GitTools/actions/gitversion/setup@7417b10'; Expected = 'gittools/actions' }
    @{ Reference = 'github/codeql-action/upload-sarif@v4'; Expected = 'github/codeql-action' }
  ) {
    param($Reference, $Expected)
    Get-ActionRepository -Reference $Reference | Should -Be $Expected
  }
}

Describe 'Get-DotnetToolInstall' {
  It 'finds the package id of every dotnet tool install or update, whatever the options' -TestCases @(
    @{ Text = 'dotnet tool install --global dotnet-sonarscanner'; Expected = 'dotnet-sonarscanner' }
    @{ Text = 'dotnet tool install -g roslynator.dotnet.cli --version 0.12.0'; Expected = 'roslynator.dotnet.cli' }
    @{ Text = 'dotnet tool update --global dotnet-coverage --version 18.12.0'; Expected = 'dotnet-coverage' }
    @{ Text = 'dotnet tool install --version 2.1.0 --tool-path ./t trx2junit'; Expected = 'trx2junit' }
    @{ Text = 'run: dotnet tool install --add-source https://x/index.json --global Some.Tool && echo done'; Expected = 'some.tool' }
  ) {
    param($Text, $Expected)
    Get-DotnetToolInstall -Text $Text | Should -Be $Expected
  }

  It 'finds nothing in a restore, which installs only what a manifest lists, or a line that is not a command' -TestCases @(
    @{ Text = 'dotnet tool restore' }
    @{ Text = 'dotnet tool list --global' }
    @{ Text = '# dotnet tool install --global commented-out' }
  ) {
    param($Text)
    @(Get-DotnetToolInstall -Text $Text).Count | Should -Be 0
  }

  It 'returns an id it cannot resolve as written, so an unlisted expression fails the check' {
    Get-DotnetToolInstall -Text 'dotnet tool install --global $TOOL' | Should -Be '$tool'
  }
}

Describe 'Get-ReleaseDownload' {
  It 'finds the owner/repo of a GitHub release asset a workflow downloads' -TestCases @(
    @{ Text = '          https://github.com/google/osv-scanner/releases/download/v1.9.2/osv-scanner_linux_amd64'; Expected = 'google/osv-scanner' }
    @{ Text = '  "https://github.com/Gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/${archive}"'; Expected = 'gitleaks/gitleaks' }
    @{ Text = 'curl -sSfL -o x https://github.com/a/b/releases/download/v1/x && curl https://github.com/c/d/releases/download/v2/y'; Expected = @('a/b', 'c/d') }
  ) {
    param($Text, $Expected)
    @(Get-ReleaseDownload -Text $Text) | Should -Be $Expected
  }

  It 'finds nothing in a comment or a link that is not a release asset' -TestCases @(
    @{ Text = '# https://github.com/a/b/releases/download/v1/x' }
    @{ Text = 'see https://github.com/a/b/releases for notes' }
  ) {
    param($Text)
    @(Get-ReleaseDownload -Text $Text).Count | Should -Be 0
  }
}

Describe 'Get-ContainerImage' {
  It 'finds the image, without tag or digest, of every way a workflow runs a container' -TestCases @(
    @{ Text = '        run: docker run --rm -v "$PWD":/data:ro fsfe/reuse:6.2.0@sha256:85462a75c0f8efda09ddd190b92816b70e7662577c8427429e11e1b9f25a992e lint'; Expected = 'fsfe/reuse' }
    @{ Text = 'docker run -e A=1 --name x --network=host -w /src ghcr.io/Owner/Tool:1.2 --flag'; Expected = 'ghcr.io/owner/tool' }
    @{ Text = 'docker run --rm localhost:5000/img'; Expected = 'localhost:5000/img' }
    @{ Text = '      - uses: docker://alpine:3.20 # pinned'; Expected = 'alpine' }
    @{ Text = "        uses: 'docker://ghcr.io/a/b@sha256:abc'"; Expected = 'ghcr.io/a/b' }
    @{ Text = '    container: node:20-bookworm'; Expected = 'node' }
    @{ Text = '        image: postgres:17@sha256:0123'; Expected = 'postgres' }
    @{ Text = "        image: 'rabbitmq:4-management'"; Expected = 'rabbitmq' }
  ) {
    param($Text, $Expected)
    Get-ContainerImage -Text $Text | Should -Be $Expected
  }

  It 'finds nothing in a comment, a block container: key, or a line that runs no container' -TestCases @(
    @{ Text = '# docker run --rm fsfe/reuse lint' }
    @{ Text = '    container:' }
    @{ Text = '    steps:' }
    @{ Text = 'docker build -t x .' }
  ) {
    param($Text)
    @(Get-ContainerImage -Text $Text).Count | Should -Be 0
  }

  It 'returns an image it cannot resolve as written, so an unlisted expression fails the check' {
    Get-ContainerImage -Text '    container: ${{ matrix.image }}' | Should -Be '${{'
  }
}

Describe 'Get-PipelineTool' {
  It 'finds every remote action in workflows and composite actions, local actions excluded, and every tool' {
    $root = New-FixtureRepository -WorkflowLines @(
      'jobs:', '  a:', '    steps:',
      '      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1',
      "      - uses: 'owner/quoted/sub@abc'",
      '      - name: x',
      '        uses: "owner/double@def"',
      '      - uses: ./.github/actions/x',
      '      - uses: docker://alpine:3',
      '      - run: |',
      '          dotnet tool install --global trx2junit --version 2.1.0',
      '          curl -sSfL -o g.tgz https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/g.tgz',
      '      - run: docker run --rm fsfe/reuse:6.2.0 lint',
      '  s:', '    container:', '      image: node:20', '    services:', '      db:', '        image: postgres:17',
      '  b:', '    uses: ./.github/workflows/reusable.yml',
      '  c:', '    uses: other-org/shared/.github/workflows/w.yml@0123456') -ListLines @()
    try {
      $tools = @(Get-PipelineTool -RepositoryRoot $root)
      $actions = @($tools | Where-Object Kind -EQ 'action' | ForEach-Object Id | Sort-Object -Unique)
      $actions | Should -Be @('actions/checkout', 'other-org/shared', 'owner/composite-tool', 'owner/double', 'owner/quoted')
      $dotnet = @($tools | Where-Object Kind -EQ 'dotnet-tool' | ForEach-Object Id | Sort-Object -Unique)
      $dotnet | Should -Be @('some.tool', 'trx2junit')
      ($tools | Where-Object Id -EQ 'trx2junit').Source | Should -BeLike '*w.yml:11'
      @($tools | Where-Object Kind -EQ 'release-binary' | ForEach-Object Id) | Should -Be @('gitleaks/gitleaks')
      ($tools | Where-Object Kind -EQ 'release-binary').Source | Should -BeLike '*w.yml:12'
      @($tools | Where-Object Kind -EQ 'container' | ForEach-Object Id | Sort-Object) | Should -Be @('alpine', 'fsfe/reuse', 'node', 'postgres')
      ($tools | Where-Object Id -EQ 'some.tool').Source | Should -BeLike '*dotnet-tools.json'
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'reads the root tool manifest as well as .config' {
    $root = New-FixtureRepository -WorkflowLines @('jobs: {}') -ListLines @()
    try {
      Set-Content -LiteralPath (Join-Path $root 'dotnet-tools.json') -Value '{ "version": 1, "tools": { "dotnet-ef": { "version": "10.0.0" } } }'
      @(Get-PipelineTool -RepositoryRoot $root | Where-Object Kind -EQ 'dotnet-tool' | ForEach-Object Id | Sort-Object -Unique) |
        Should -Be @('dotnet-ef', 'some.tool')
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'reads a manifest with no tools as none' {
    $root = New-FixtureRepository -WorkflowLines @('jobs: {}') -ListLines @() -Manifest '{ "version": 1, "isRoot": true }'
    try {
      @(Get-PipelineTool -RepositoryRoot $root | Where-Object Kind -EQ 'dotnet-tool').Count | Should -Be 0
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }
}

Describe 'Test-StrongCopyleft' {
  It 'names the strong-copyleft families, any version or variant' -TestCases @(
    @{ License = 'AGPL-3.0' }; @{ License = 'AGPL-3.0-only' }; @{ License = 'AGPL-3.0-or-later' }
    @{ License = 'GPL-2.0' }; @{ License = 'GPL-2.0-only' }; @{ License = 'GPL-3.0-or-later' }; @{ License = 'gpl-3.0' }
    @{ License = 'SSPL-1.0' }; @{ License = 'MIT AND GPL-3.0-only' }; @{ License = 'GPL-2.0-only WITH Classpath-exception-2.0' }
  ) {
    param($License)
    Test-StrongCopyleft -License $License | Should -BeTrue
  }

  It 'does not count weak copyleft or permissive licenses' -TestCases @(
    @{ License = 'LGPL-3.0-only' }; @{ License = 'LGPL-2.1-or-later' }; @{ License = 'MIT' }; @{ License = 'Apache-2.0' }
    @{ License = 'LicenseRef-Microsoft-dotnet-coverage' }
  ) {
    param($License)
    Test-StrongCopyleft -License $License | Should -BeFalse
  }
}

Describe 'Get-ToolLicenseViolation' {
  BeforeAll {
    function Entry([string]$Kind, [string]$Id, [string]$License, [bool]$Exception = $false, [string]$Reason = 'why') {
      [pscustomobject]@{ Kind = $Kind; Id = $Id; License = $License; Exception = $Exception; Reason = $Reason; Line = 1 }
    }
    function Tool([string]$Kind, [string]$Id) { [pscustomobject]@{ Kind = $Kind; Id = $Id; Source = 'w.yml:1' } }
  }

  It 'passes listed tools under allowed licenses, and a reviewed exception' {
    $list = @((Entry 'action' 'a/b' 'MIT'), (Entry 'dotnet-tool' 'scanner' 'LGPL-3.0-only' $true 'only client'))
    @(Get-ToolLicenseViolation -Tools @((Tool 'action' 'a/b'), (Tool 'dotnet-tool' 'scanner')) -List $list).Count | Should -Be 0
  }

  It 'accepts each permissive license the policy allows' -TestCases @(
    @{ License = 'MIT' }; @{ License = 'Apache-2.0' }; @{ License = 'BSD-2-Clause' }; @{ License = 'BSD-3-Clause' }; @{ License = 'PostgreSQL' }
  ) {
    param($License)
    @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'a/b') -List @(Entry 'action' 'a/b' $License)).Count | Should -Be 0
  }

  It 'fails a tool the list does not name, saying where it is used' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'new/tool') -List @())
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*action new/tool*not in*w.yml:1*'
  }

  It 'fails a tool whose kind differs from the entry with its id' {
    @(Get-ToolLicenseViolation -Tools @(Tool 'dotnet-tool' 'a/b') -List @(Entry 'action' 'a/b' 'MIT')).Count | Should -BeGreaterThan 0
  }

  It 'passes strong copyleft only on the one CI-only container run unmodified, marked as an exception' {
    $list = @(Entry 'container' 'fsfe/reuse' 'Apache-2.0 AND GPL-3.0-or-later' $true 'run unmodified')
    @(Get-ToolLicenseViolation -Tools @(Tool 'container' 'fsfe/reuse') -List $list).Count | Should -Be 0
  }

  It 'fails strong copyleft on that container without the exception marker' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'container' 'fsfe/reuse') -List @(Entry 'container' 'fsfe/reuse' 'GPL-3.0-or-later'))
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*fsfe/reuse*strong copyleft*'
  }

  It 'fails strong copyleft on any other container, exception or not' -TestCases @(
    @{ Exception = $false }; @{ Exception = $true }
  ) {
    param($Exception)
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'container' 'other/image') -List @(Entry 'container' 'other/image' 'AGPL-3.0' $Exception))
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*other/image*strong copyleft*'
  }

  It 'fails a strong-copyleft license, even when it is marked as an exception' -TestCases @(
    @{ Exception = $false }; @{ Exception = $true }
  ) {
    param($Exception)
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'trufflesecurity/trufflehog') -List @(Entry 'action' 'trufflesecurity/trufflehog' 'AGPL-3.0' $Exception))
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*trufflesecurity/trufflehog*AGPL-3.0*strong copyleft*'
  }

  It 'fails any other license that is not marked as a reviewed exception' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'a/b') -List @(Entry 'action' 'a/b' 'MPL-2.0'))
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*a/b*MPL-2.0*exception*'
  }

  It 'fails an entry without a reason' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'a/b') -List @(Entry 'action' 'a/b' 'MIT' $false ''))
    $v[0] | Should -BeLike '*a/b*reason*'
  }

  It 'fails an entry nothing uses, so the list stays the set of tools in use' {
    $v = @(Get-ToolLicenseViolation -Tools @() -List @(Entry 'action' 'gone/tool' 'MIT'))
    $v.Count | Should -Be 1
    $v[0] | Should -BeLike '*gone/tool*nothing uses*'
  }

  It 'fails an exception marker on a license that needs none, so a marker never outlives its reason' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'a/b') -List @(Entry 'action' 'a/b' 'MIT' $true))
    $v[0] | Should -BeLike '*a/b*exception*not needed*'
  }

  It 'fails an id listed twice' {
    $v = @(Get-ToolLicenseViolation -Tools @(Tool 'action' 'a/b') -List @((Entry 'action' 'a/b' 'MIT'), (Entry 'action' 'a/b' 'MIT')))
    $v[0] | Should -BeLike '*a/b*more than once*'
  }
}

Describe 'Get-ReviewConfigCopyleft' {
  It 'names every strong-copyleft license the dependency review allow list would let in' {
    $dir = New-TempDirectory
    try {
      $path = Join-Path $dir 'c.yml'
      Set-Content -LiteralPath $path -Value @('allow-licenses:', '  - MIT', '  - GPL-3.0-only  # no', '  - LGPL-3.0-only', 'allow-dependencies-licenses:', '  - pkg:npm/x')
      @(Get-ReviewConfigCopyleft -Path $path) | Should -Be @('GPL-3.0-only')
    } finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
  }
}

Describe 'The script' {
  It 'passes a fixture whose tools are all listed' {
    $root = New-FixtureRepository -WorkflowLines $BaseWorkflow -ListLines $BaseList
    try {
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be 0 -Because ($out -join "`n")
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'fails a fixture that adds an action the list does not name' {
    $root = New-FixtureRepository -WorkflowLines ($BaseWorkflow + '      - uses: someone/new-action@0123456789abcdef0123456789abcdef01234567 # v1') -ListLines $BaseList
    try {
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be 1
      ($out -join "`n") | Should -BeLike '*::error::*someone/new-action*'
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'fails a fixture that lists an AGPL tool, the case that canceled a release' {
    $root = New-FixtureRepository -WorkflowLines ($BaseWorkflow + '      - uses: trufflesecurity/trufflehog@31ce31ee1abbcc7dc8eee5764c17121c3044c8b2 # v3.99.7') `
      -ListLines ($BaseList + 'action  trufflesecurity/trufflehog  AGPL-3.0  exception  # secret scanning')
    try {
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be 1
      ($out -join "`n") | Should -BeLike '*::error::*trufflesecurity/trufflehog*AGPL-3.0*'
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'fails a fixture that runs a container image the list does not name' {
    $root = New-FixtureRepository -WorkflowLines ($BaseWorkflow + '      - run: docker run --rm someone/scanner:1.0 scan') -ListLines $BaseList
    try {
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be 1
      ($out -join "`n") | Should -BeLike '*::error::container someone/scanner is not in*'
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'passes the REUSE image only as the listed exception' -TestCases @(
    @{ Marker = 'exception'; Exit = 0 }
    @{ Marker = ''; Exit = 1 }
  ) {
    param($Marker, $Exit)
    $root = New-FixtureRepository -WorkflowLines ($BaseWorkflow + '      - run: docker run --rm -v "$PWD":/data:ro fsfe/reuse:6.2.0@sha256:85462a75 lint') `
      -ListLines ($BaseList + "container  fsfe/reuse  GPL-3.0-or-later  $Marker  # license headers, run unmodified")
    try {
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be $Exit -Because ($out -join "`n")
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }

  It 'fails a fixture whose dependency review allow list admits strong copyleft' {
    $root = New-FixtureRepository -WorkflowLines $BaseWorkflow -ListLines $BaseList
    try {
      Set-Content -LiteralPath (Join-Path $root '.github' 'dependency-review-config.yml') -Value @('allow-licenses:', '  - MIT', '  - AGPL-3.0-only')
      $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' 'Test-PipelineToolLicenses.ps1') -RepositoryRoot $root 2>&1
      $LASTEXITCODE | Should -Be 1
      ($out -join "`n") | Should -BeLike '*::error::AGPL-3.0-only*dependency-review-config.yml*'
    } finally { Remove-Item -Recurse -Force -LiteralPath $root -ErrorAction SilentlyContinue }
  }
}

Describe 'This repository' {
  BeforeAll {
    $script:List = @(Read-ToolLicenseList -Path (Join-Path $Root '.github' 'pipeline-tool-licenses.txt'))
    $script:Tools = @(Get-PipelineTool -RepositoryRoot $Root)
  }

  It 'uses actions and dotnet tools' {
    @($Tools | Where-Object Kind -EQ 'action').Count | Should -BeGreaterThan 50
    @($Tools | Where-Object Kind -EQ 'dotnet-tool').Count | Should -BeGreaterThan 5
    @($Tools | Where-Object Kind -EQ 'release-binary').Count | Should -BeGreaterThan 1
    @($Tools | Where-Object Kind -EQ 'container').Count | Should -BeGreaterThan 0
  }

  It 'lists every pipeline tool under an allowed license or a reviewed exception, and nothing unused' {
    @(Get-ToolLicenseViolation -Tools $Tools -List $List) | Should -BeNullOrEmpty
  }

  It 'excepts exactly the three reviewed tools: the SonarCloud scanner, the Microsoft coverage tool and the REUSE image' {
    @($List | Where-Object Exception | ForEach-Object Id | Sort-Object) | Should -Be @('dotnet-coverage', 'dotnet-sonarscanner', 'fsfe/reuse')
  }

  It 'lets strong copyleft in only for the REUSE container, the one reviewed CI-only image run unmodified' {
    @($List | Where-Object { Test-StrongCopyleft -License $_.License } | ForEach-Object { "$($_.Kind) $($_.Id)" }) | Should -Be @('container fsfe/reuse')
    @($script:CopyleftContainerExceptions) | Should -Be @('fsfe/reuse')
  }

  It 'keeps strong copyleft off the dependency review allow list' {
    @(Get-ReviewConfigCopyleft -Path (Join-Path $Root '.github' 'dependency-review-config.yml')) | Should -BeNullOrEmpty
  }
}
