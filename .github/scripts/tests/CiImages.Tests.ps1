#Requires -Modules Pester

# The CI image mirror: the manifest, the prefix rule, the source selection, the copy, and the drift guard
# (Test-CiImagePulls.ps1) that fails a job pulling a Docker Hub image the manifest does not list.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Scripts = Join-Path $Root '.github' 'scripts'
  Import-Module (Join-Path $Root 'scripts' 'lib' 'CiImages.psm1') -Force

  $script:MirrorPrefix = 'ghcr.io/whizbang-lib/ci-mirror/'
  $script:DigestA = 'sha256:' + ('a' * 64)
  $script:DigestB = 'sha256:' + ('b' * 64)

  # A manifest file with the given lines; returns its path.
  function New-Manifest {
    param([string[]]$Lines)
    $path = Join-Path $TestDrive "ci-images-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content -LiteralPath $path -Value $Lines
    $path
  }

  # Runs a script in a child pwsh with a fake `docker` (a shell script) first on PATH; returns its output
  # and leaves its exit code in $LASTEXITCODE. Covers each script's entry point as CI runs it.
  function Invoke-WithFakeDocker {
    param([string]$FakeDocker, [string]$Script, [string[]]$Arguments)
    $bin = Join-Path $TestDrive "bin-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $bin | Out-Null
    Set-Content -LiteralPath (Join-Path $bin 'docker') -Value "#!/bin/sh`n$FakeDocker"
    chmod +x (Join-Path $bin 'docker')
    $saved = $env:PATH
    try {
      $env:PATH = "$bin$([IO.Path]::PathSeparator)$saved"
      pwsh -NoProfile -File (Join-Path $Scripts $Script) @Arguments
    } finally {
      $env:PATH = $saved
    }
  }

  $script:TwoImages = @(
    '# comment', '',
    "pgvector/pgvector:pg17@$DigestA",
    "  rabbitmq:3.13-management-alpine@$DigestB  "
  )
}

Describe 'Test-CiImageHasRegistry' {
  It 'treats <Image> as <Expected>' -ForEach @(
    @{ Image = 'rabbitmq:3'; Expected = $false }
    @{ Image = 'pgvector/pgvector:pg17'; Expected = $false }
    @{ Image = 'mcr.microsoft.com/azure-storage/azurite:latest'; Expected = $true }
    @{ Image = 'localhost:5000/x:1'; Expected = $true }
    @{ Image = 'localhost/x:1'; Expected = $true }
  ) {
    Test-CiImageHasRegistry -Image $Image | Should -Be $Expected
  }
}

Describe 'Resolve-CiImage' {
  It 'leaves a Docker Hub image alone without a prefix' {
    Resolve-CiImage -Image 'pgvector/pgvector:pg17' -Prefix '' | Should -Be 'pgvector/pgvector:pg17'
  }

  It 'prefixes a Docker Hub image, with or without the trailing slash' {
    Resolve-CiImage -Image 'rabbitmq:3' -Prefix $MirrorPrefix | Should -Be 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3'
    Resolve-CiImage -Image 'rabbitmq:3' -Prefix ' ghcr.io/whizbang-lib/ci-mirror ' | Should -Be 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3'
  }

  It 'never prefixes an image from another registry' {
    Resolve-CiImage -Image 'mcr.microsoft.com/mssql/server:2022-latest' -Prefix $MirrorPrefix |
      Should -Be 'mcr.microsoft.com/mssql/server:2022-latest'
  }

  It 'reads the prefix from TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX by default' {
    $saved = $env:TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX
    try {
      $env:TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX = $MirrorPrefix
      Resolve-CiImage -Image 'rabbitmq:3' | Should -Be 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3'
      $env:TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX = $null
      Resolve-CiImage -Image 'rabbitmq:3' | Should -Be 'rabbitmq:3'
    } finally {
      $env:TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX = $saved
    }
  }

  It 'names the variable Testcontainers reads' {
    Get-CiImagePrefixVariable | Should -Be 'TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX'
  }
}

Describe 'Read-CiImageManifest' {
  It 'reads name, tag, digest and the pinned source, skipping comments and blanks' {
    $images = Read-CiImageManifest -Path (New-Manifest $TwoImages)
    $images.Count | Should -Be 2
    $images[0].Name | Should -Be 'pgvector/pgvector'
    $images[0].Tag | Should -Be 'pg17'
    $images[0].Digest | Should -Be $DigestA
    $images[0].Image | Should -Be 'pgvector/pgvector:pg17'
    $images[1].Source | Should -Be "docker.io/rabbitmq:3.13-management-alpine@$DigestB"
  }

  It 'rejects a line without a digest, naming the line' {
    { Read-CiImageManifest -Path (New-Manifest @('# x', 'rabbitmq:3')) } | Should -Throw '*:2:*rabbitmq:3*'
  }

  It 'rejects an image from another registry' {
    { Read-CiImageManifest -Path (New-Manifest @("mcr.microsoft.com/x:1@$DigestA")) } | Should -Throw '*names a registry*'
  }

  It 'rejects an image listed twice' {
    { Read-CiImageManifest -Path (New-Manifest @("rabbitmq:3@$DigestA", "rabbitmq:3@$DigestB")) } | Should -Throw "*'rabbitmq:3' is listed more than once*"
  }

  It 'reads an empty manifest as no images' {
    @(Read-CiImageManifest -Path (New-Manifest @('# nothing'))).Count | Should -Be 0
  }
}

Describe 'ConvertTo-CiImageName' {
  It 'reduces <Reference> to <Expected>' -ForEach @(
    @{ Reference = 'rabbitmq:3'; Expected = 'rabbitmq:3' }
    @{ Reference = 'docker.io/library/rabbitmq:3'; Expected = 'rabbitmq:3' }
    @{ Reference = 'library/rabbitmq:3'; Expected = 'rabbitmq:3' }
    @{ Reference = 'registry-1.docker.io/pgvector/pgvector:pg17'; Expected = 'pgvector/pgvector:pg17' }
    @{ Reference = 'ghcr.io/whizbang-lib/ci-mirror/testcontainers/ryuk:0.14.0'; Expected = 'testcontainers/ryuk:0.14.0' }
    @{ Reference = "rabbitmq:3@$('sha256:' + ('c' * 64))"; Expected = 'rabbitmq:3' }
    @{ Reference = 'mcr.microsoft.com/azure-storage/azurite:latest'; Expected = $null }
    @{ Reference = 'ghcr.io/someone-else/x:1'; Expected = $null }
    @{ Reference = '<none>:<none>'; Expected = $null }
    @{ Reference = 'rabbitmq:<none>'; Expected = $null }
    @{ Reference = 'localhost:5000/x'; Expected = $null }
  ) {
    ConvertTo-CiImageName -Reference $Reference -Prefix $MirrorPrefix | Should -Be $Expected
  }

  It 'treats a mirror-looking name as a registry image when no prefix is in effect' {
    ConvertTo-CiImageName -Reference 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3' -Prefix '' | Should -BeNullOrEmpty
  }
}

Describe 'Find-UnlistedCiImage' {
  BeforeAll { $script:Manifest = Read-CiImageManifest -Path (New-Manifest $TwoImages) }

  It 'passes listed images, mirror copies and other registries' {
    $pulled = @('pgvector/pgvector:pg17', 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3.13-management-alpine',
      'mcr.microsoft.com/azure-storage/azurite:latest', '<none>:<none>')
    @(Find-UnlistedCiImage -Pulled $pulled -Manifest $Manifest -Prefix $MirrorPrefix).Count | Should -Be 0
  }

  It 'names each unlisted Docker Hub image once, mirrored or not' {
    $pulled = @('redis:7', 'docker.io/library/redis:7', 'ghcr.io/whizbang-lib/ci-mirror/testcontainers/ryuk:0.15.0')
    Find-UnlistedCiImage -Pulled $pulled -Manifest $Manifest -Prefix $MirrorPrefix |
      Should -Be @('redis:7', 'testcontainers/ryuk:0.15.0')
  }

  It 'catches a tag the manifest does not list for a listed name' {
    Find-UnlistedCiImage -Pulled @('pgvector/pgvector:pg18') -Manifest $Manifest -Prefix '' | Should -Be 'pgvector/pgvector:pg18'
  }
}

Describe 'Test-CiImagePulls.ps1 (the drift guard)' {
  BeforeAll {
    . (Join-Path $Scripts 'Test-CiImagePulls.ps1')
    $script:ManifestFile = New-Manifest $TwoImages
    $script:Baseline = Join-Path $TestDrive 'baseline.txt'
  }

  It 'passes when every pulled Docker Hub image is listed' {
    Set-Content -LiteralPath $Baseline -Value @('ubuntu:24.04')
    $out = Invoke-TestCiImagePulls -ManifestPath $ManifestFile -Prefix $MirrorPrefix -BaselinePath $Baseline `
      -ListImages { 'ubuntu:24.04', 'ghcr.io/whizbang-lib/ci-mirror/pgvector/pgvector:pg17', '' }
    $out[-1] | Should -Be 0
    $out[0] | Should -BeLike '*1 image(s) pulled*'
  }

  It 'FAILS on a new unlisted Docker Hub image and says how to fix it' {
    $out = Invoke-TestCiImagePulls -ManifestPath $ManifestFile -Prefix '' -BaselinePath (Join-Path $TestDrive 'absent.txt') `
      -ListImages { 'pgvector/pgvector:pg17', 'redis:7-alpine' }
    $out[-1] | Should -Be 1
    $out[0] | Should -BeLike '::error *redis:7-alpine was pulled from Docker Hub*ci-images.txt*'
  }

  It 'ignores an unlisted image the runner already held before the tests' {
    Set-Content -LiteralPath $Baseline -Value @('redis:7-alpine')
    $out = Invoke-TestCiImagePulls -ManifestPath $ManifestFile -Prefix '' -BaselinePath $Baseline -ListImages { 'redis:7-alpine' }
    $out[-1] | Should -Be 0
  }

  It 'lists local images through docker by default' {
    Mock docker { 'pgvector/pgvector:pg17' }
    $out = Invoke-TestCiImagePulls -ManifestPath $ManifestFile -Prefix '' -BaselinePath (Join-Path $TestDrive 'absent.txt')
    $out[-1] | Should -Be 0
    Should -Invoke docker -Times 1 -ParameterFilter { $args -contains 'ls' }
  }

  It 'exits 1 as a script when an unlisted image was pulled, and 0 when none was' -Skip:$IsWindows {
    $absent = Join-Path $TestDrive 'absent.txt'
    $out = Invoke-WithFakeDocker -FakeDocker 'echo redis:7' -Script 'Test-CiImagePulls.ps1' `
      -Arguments @('-ManifestPath', $ManifestFile, '-Prefix', '', '-BaselinePath', $absent)
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '::error *redis:7*'
    $out = Invoke-WithFakeDocker -FakeDocker 'echo pgvector/pgvector:pg17' -Script 'Test-CiImagePulls.ps1' `
      -Arguments @('-ManifestPath', $ManifestFile, '-Prefix', '', '-BaselinePath', $absent)
    $LASTEXITCODE | Should -Be 0
    "$out" | Should -BeLike 'Every Docker Hub image*'
  }
}

Describe 'Select-CiImageSource.ps1' {
  BeforeAll {
    . (Join-Path $Scripts 'Select-CiImageSource.ps1')
    $script:ManifestFile = New-Manifest $TwoImages
  }

  BeforeEach {
    $script:EnvPath = Join-Path $TestDrive "env-$([guid]::NewGuid().ToString('N'))"
    $script:Baseline = Join-Path $TestDrive "baseline-$([guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $EnvPath -Value @()
  }

  It 'switches to the mirror when every copy is there at the manifest digest, and records the baseline' {
    $digests = @{
      'ghcr.io/whizbang-lib/ci-mirror/pgvector/pgvector:pg17' = $DigestA
      'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3.13-management-alpine' = $DigestB
    }
    $out = Invoke-SelectCiImageSource -ManifestPath $ManifestFile -Prefix $MirrorPrefix -BaselinePath $Baseline -EnvFile $EnvPath `
      -GetDigest { param($r) $digests[$r] }.GetNewClosure() -ListImages { 'ubuntu:24.04' }
    Get-Content -LiteralPath $EnvPath | Should -Be "TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=$MirrorPrefix"
    Get-Content -LiteralPath $Baseline | Should -Be 'ubuntu:24.04'
    $out | Should -BeLike 'Pulling Docker Hub images from the mirror*'
  }

  It 'falls back to Docker Hub, with a warning naming the gap, when a copy is missing or stale' {
    $digests = @{ 'ghcr.io/whizbang-lib/ci-mirror/pgvector/pgvector:pg17' = $DigestB }
    $out = Invoke-SelectCiImageSource -ManifestPath $ManifestFile -Prefix $MirrorPrefix -BaselinePath $Baseline -EnvFile $EnvPath `
      -GetDigest { param($r) $digests[$r] }.GetNewClosure() -ListImages { @() }
    Get-Content -LiteralPath $EnvPath | Should -BeNullOrEmpty
    $out | Should -BeLike '::warning *pgvector/pgvector:pg17 (has sha256:bbbb*wants sha256:aaaa*rabbitmq:3.13-management-alpine (has nothing readable*'
  }

  It 'writes nothing when there is no env file to write' {
    $out = Invoke-SelectCiImageSource -ManifestPath $ManifestFile -Prefix $MirrorPrefix -BaselinePath $Baseline -EnvFile '' `
      -GetDigest { param($r) if ($r -like '*pgvector*') { $DigestA } else { $DigestB } } -ListImages { @() }
    $out | Should -BeLike 'Pulling Docker Hub images from the mirror*'
  }

  It 'reads digests and local images through docker by default' {
    Mock docker { $global:LASTEXITCODE = 0; if ($args -contains 'ls') { 'ubuntu:24.04' } elseif ($args -match 'pgvector') { $DigestA } else { $DigestB } }
    $null = Invoke-SelectCiImageSource -ManifestPath $ManifestFile -Prefix $MirrorPrefix -BaselinePath $Baseline -EnvFile $EnvPath
    Get-Content -LiteralPath $EnvPath | Should -Be "TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=$MirrorPrefix"
    Get-Content -LiteralPath $Baseline | Should -Be 'ubuntu:24.04'
  }

  It 'reads an unreadable mirror copy as missing' {
    Mock docker { $global:LASTEXITCODE = 1 }
    Get-RemoteImageDigest -Reference 'ghcr.io/x/y:1' | Should -BeNullOrEmpty
  }
}

Describe 'Copy-CiImages.ps1' {
  BeforeAll {
    . (Join-Path $Scripts 'Copy-CiImages.ps1')
    $script:ManifestFile = New-Manifest @("rabbitmq:3@$DigestA")
    $script:Target = 'ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3'
  }

  It 'skips a copy that already carries the manifest digest' {
    $copies = [Collections.Generic.List[string]]::new()
    $out = Invoke-CopyCiImages -ManifestPath $ManifestFile -Prefix $MirrorPrefix -GetDigest { $DigestA } `
      -Copy { param($s, $t) $copies.Add($t); $true }.GetNewClosure()
    $out[-1] | Should -Be 0
    $copies.Count | Should -Be 0
    $out[0] | Should -BeLike "up to date: $Target*"
  }

  It 'copies the pinned source to the mirror name and verifies the digest' {
    $state = @{ Digest = $null; Source = $null; Target = $null; Want = $DigestA }
    $out = Invoke-CopyCiImages -ManifestPath $ManifestFile -Prefix $MirrorPrefix -RetryDelaySeconds 0 -GetDigest { $state.Digest }.GetNewClosure() `
      -Copy { param($s, $t) $state.Source = $s; $state.Target = $t; $state.Digest = $state.Want; $true }.GetNewClosure()
    $out[-1] | Should -Be 0
    $state.Source | Should -Be "docker.io/rabbitmq:3@$DigestA"
    $state.Target | Should -Be $Target
    $out | Should -Contain "copied: $Target ($DigestA)"
  }

  It 'retries a failed copy and succeeds on a later attempt' {
    $state = @{ Calls = 0; Digest = $null; Want = $DigestA }
    $out = Invoke-CopyCiImages -ManifestPath $ManifestFile -Prefix $MirrorPrefix -Attempts 3 -RetryDelaySeconds 0 `
      -GetDigest { $state.Digest }.GetNewClosure() `
      -Copy { $state.Calls++; if ($state.Calls -ge 2) { $state.Digest = $state.Want; $true } else { $false } }.GetNewClosure()
    $out[-1] | Should -Be 0
    $state.Calls | Should -Be 2
    $out | Should -Contain '  not copied yet; retrying in 0s'
  }

  It 'fails when the copy never lands at the manifest digest' {
    $out = Invoke-CopyCiImages -ManifestPath $ManifestFile -Prefix $MirrorPrefix -Attempts 2 -RetryDelaySeconds 0 `
      -GetDigest { $DigestB } -Copy { $true }
    $out[-1] | Should -Be 1
    $out | Should -Contain "::error title=CI image not mirrored::docker.io/rabbitmq:3@$DigestA could not be copied to $Target at its digest after 2 attempt(s)."
  }

  It 'copies and reads through docker by default' {
    Mock docker { $global:LASTEXITCODE = 0; if ($args -contains 'inspect') { $DigestA } }
    $out = Invoke-CopyCiImages -ManifestPath $ManifestFile -Prefix $MirrorPrefix
    $out[-1] | Should -Be 0
    Copy-RemoteImage -Source 'docker.io/rabbitmq:3' -Target $Target | Should -BeTrue
    Should -Invoke docker -ParameterFilter { $args -contains 'create' -and $args -contains $Target }
  }

  It 'reads an absent mirror copy as missing, so it is copied' {
    Mock docker { $global:LASTEXITCODE = 1 }
    Get-RemoteImageDigest -Reference $Target | Should -BeNullOrEmpty
  }
}

Describe 'The scripts as CI runs them' -Skip:$IsWindows {
  BeforeAll {
    $script:ManifestFile = New-Manifest $TwoImages
    # Answers `image ls` with one image and `imagetools inspect <ref>` with the manifest digest.
    $script:MirrorComplete = @"
case "`$*" in
  *" ls "*) echo ubuntu:24.04 ;;
  *pgvector*) echo $DigestA ;;
  *rabbitmq*) echo $DigestB ;;
esac
"@
  }

  It 'Select-CiImageSource.ps1 writes the prefix to GITHUB_ENV and exits 0' {
    $envPath = Join-Path $TestDrive 'github-env'
    $null = Invoke-WithFakeDocker -FakeDocker $MirrorComplete -Script 'Select-CiImageSource.ps1' `
      -Arguments @('-ManifestPath', $ManifestFile, '-BaselinePath', (Join-Path $TestDrive 'b.txt'), '-EnvFile', $envPath)
    $LASTEXITCODE | Should -Be 0
    Get-Content -LiteralPath $envPath | Should -Be 'TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=ghcr.io/whizbang-lib/ci-mirror/'
  }

  It 'Copy-CiImages.ps1 reports each image up to date and exits 0' {
    $out = Invoke-WithFakeDocker -FakeDocker $MirrorComplete -Script 'Copy-CiImages.ps1' -Arguments @('-ManifestPath', $ManifestFile)
    $LASTEXITCODE | Should -Be 0
    @($out | Where-Object { $_ -like 'up to date:*' }).Count | Should -Be 2
  }

  It 'Copy-CiImages.ps1 exits 1 when a copy cannot be made' {
    $out = Invoke-WithFakeDocker -FakeDocker 'exit 1' -Script 'Copy-CiImages.ps1' `
      -Arguments @('-ManifestPath', $ManifestFile, '-Attempts', '1', '-RetryDelaySeconds', '0')
    $LASTEXITCODE | Should -Be 1
    @($out | Where-Object { $_ -like '::error *' }).Count | Should -Be 2
  }
}

Describe 'The committed manifest and its consumers' {
  BeforeAll {
    $script:Committed = Read-CiImageManifest -Path (Join-Path $Root '.github' 'ci-images.txt')
    $script:Listed = @($Committed | ForEach-Object Image)
  }

  It 'lists the Testcontainers reaper' {
    @($Listed | Where-Object { $_ -like 'testcontainers/ryuk:*' }).Count | Should -Be 1
  }

  It 'lists every image the C# fixtures name (CiImages.cs)' {
    $source = Get-Content -Raw (Join-Path $Root 'src' 'Whizbang.Testing' 'Containers' 'CiImages.cs')
    $named = [regex]::Matches($source, 'public const string (?!PREFIX_VARIABLE)\w+ = "([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    @($named).Count | Should -BeGreaterThan 0
    foreach ($image in $named) { $Listed | Should -Contain $image }
  }

  It 'lists every image Run-Tests.ps1 starts, and starts each through Resolve-CiImage' {
    $source = Get-Content -Raw (Join-Path $Root 'scripts' 'Run-Tests.ps1')
    $named = [regex]::Matches($source, "Resolve-CiImage -Image '([^']+)'") | ForEach-Object { $_.Groups[1].Value }
    @($named).Count | Should -Be 2
    foreach ($image in $named) { $Listed | Should -Contain $image }
    $source | Should -Not -Match '(?m)^\s+(pgvector/pgvector|rabbitmq):\S+ `?$'
  }

  It 'is mirrored and checked in every container suite: <_>' -ForEach @('postgres', 'inmemory', 'rabbitmq', 'servicebus', 'azureblob', 'integration') {
    $workflow = Get-Content -Raw (Join-Path $Root '.github' 'workflows' "reusable-test-$_.yml")
    $workflow | Should -Match 'packages: read'
    $workflow | Should -Match 'uses: \./\.github/actions/use-ci-image-mirror'
    $workflow | Should -Match 'run: \./\.github/scripts/Test-CiImagePulls\.ps1'
    $workflow.IndexOf('use-ci-image-mirror') | Should -BeLessThan $workflow.IndexOf('run: pwsh scripts/Run-Tests.ps1')
  }
}
