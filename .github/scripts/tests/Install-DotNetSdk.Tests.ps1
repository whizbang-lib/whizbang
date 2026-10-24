# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The Windows half of .github/actions/setup-dotnet-retry. A team Windows machine has no bash on the runner
# service's PATH (or only the WSL stub), so the action's bash step failed every job routed there before
# anything else ran. Install-DotNetSdk.ps1 makes the same choice the bash step makes (the global.json
# feature band, else the input's channel) and installs with dotnet-install.ps1. The download and the
# install are replaced by scriptblocks here.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Install-DotNetSdk.ps1'
  . $ScriptPath

  function New-GlobalJson([string]$Content) {
    $path = Join-Path $TestDrive "global-$([guid]::NewGuid().ToString('N')).json"
    Set-Content -LiteralPath $path -Value $Content
    $path
  }
}

Describe 'Get-DotNetChannel' {
  It 'is the feature band of global.json''s sdk.version (<Version> -> <Channel>)' -ForEach @(
    @{ Version = '10.0.100'; Channel = '10.0.1xx' }
    @{ Version = '10.0.203'; Channel = '10.0.2xx' }
    @{ Version = '9.0.310'; Channel = '9.0.3xx' }
  ) {
    $json = New-GlobalJson "{ `"sdk`": { `"version`": `"$Version`", `"rollForward`": `"latestFeature`" } }"
    (Get-DotNetChannel -GlobalJsonPath $json -FallbackVersion '10.0.x').Channel | Should -Be $Channel
  }

  It 'falls back to the input channel, without its .x, when <Case>' -ForEach @(
    @{ Case = 'there is no global.json'; Content = $null }
    @{ Case = 'global.json has no sdk.version'; Content = '{ "msbuild-sdks": {} }' }
    @{ Case = 'global.json is not JSON'; Content = 'not json' }
    @{ Case = 'sdk.version is empty'; Content = '{ "sdk": { "version": "" } }' }
  ) {
    $path = if ($null -eq $Content) { Join-Path $TestDrive 'absent.json' } else { New-GlobalJson $Content }
    $c = Get-DotNetChannel -GlobalJsonPath $path -FallbackVersion '10.0.x'
    $c.Channel | Should -Be '10.0'
    $c.Source | Should -BeLike '*input*'
  }

  It 'keeps a fallback channel that has no .x' {
    (Get-DotNetChannel -GlobalJsonPath (Join-Path $TestDrive 'absent.json') -FallbackVersion 'STS').Channel | Should -Be 'STS'
  }

  It 'agrees with the committed global.json' {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
    $version = (Get-Content -Raw (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version
    (Get-DotNetChannel -GlobalJsonPath (Join-Path $root 'global.json') -FallbackVersion '10.0.x').Channel |
      Should -Be ($version.Substring(0, $version.Length - 2) + 'xx')
  }
}

Describe 'Install-DotNetSdk' {
  BeforeEach {
    $script:Calls = [System.Collections.Generic.List[string]]::new()
    $script:PathFile = Join-Path $TestDrive "path-$([guid]::NewGuid().ToString('N'))"
    $script:EnvFile = Join-Path $TestDrive "env-$([guid]::NewGuid().ToString('N'))"
    $calls = $script:Calls
    $script:Download = { param($Uri, $OutFile) $calls.Add("download $Uri -> $OutFile"); Set-Content -LiteralPath $OutFile -Value '# installer' }.GetNewClosure()
    $script:Install = { param($Script, $Channel, $InstallDir) $calls.Add("install $Channel into $InstallDir with $Script") }.GetNewClosure()
    $script:Version = { param($Dotnet) $calls.Add("version $Dotnet"); '10.0.100' }.GetNewClosure()
  }

  It 'downloads dotnet-install.ps1, installs the channel, and puts the SDK and its tools on PATH' {
    $json = New-GlobalJson '{ "sdk": { "version": "10.0.100" } }'
    $dir = Join-Path $TestDrive 'dotnet'
    $out = @(Install-DotNetSdk -GlobalJsonPath $json -FallbackVersion '10.0.x' -InstallDir $dir -TempDir $TestDrive `
        -PathFile $PathFile -EnvFile $EnvFile -Download $Download -Install $Install -GetVersion $Version)
    $Calls[0] | Should -BeLike 'download https://dot.net/v1/dotnet-install.ps1 -> *dotnet-install.ps1'
    $Calls[1] | Should -BeLike "install 10.0.1xx into $dir with *dotnet-install.ps1"
    $Calls[2] | Should -BeLike "version $dir*dotnet*"
    Get-Content -LiteralPath $PathFile | Should -Be @($dir, (Join-Path $dir 'tools'))
    Get-Content -LiteralPath $EnvFile | Should -Be @("DOTNET_ROOT=$dir")
    ($out -join "`n") | Should -BeLike '*feature band 10.0.1xx (from global.json version 10.0.100)*'
    ($out -join "`n") | Should -BeLike '*10.0.100*'
  }

  It 'throws, so the retry tries again, when the install fails' {
    $failing = { param($Script, $Channel, $InstallDir) throw 'network down' }
    { Install-DotNetSdk -GlobalJsonPath (Join-Path $TestDrive 'absent.json') -FallbackVersion '10.0.x' -InstallDir (Join-Path $TestDrive 'd') `
        -TempDir $TestDrive -PathFile $PathFile -EnvFile $EnvFile -Download $Download -Install $failing -GetVersion $Version } |
      Should -Throw '*network down*'
    Test-Path -LiteralPath $PathFile | Should -BeFalse
  }

  It 'throws when the installed dotnet does not run' {
    $broken = { param($Dotnet) throw 'not found' }
    { Install-DotNetSdk -GlobalJsonPath (Join-Path $TestDrive 'absent.json') -FallbackVersion '10.0.x' -InstallDir (Join-Path $TestDrive 'd') `
        -TempDir $TestDrive -PathFile $PathFile -EnvFile $EnvFile -Download $Download -Install $Install -GetVersion $broken } |
      Should -Throw '*not found*'
  }

  It 'writes nothing to runner files it was not given' {
    { Install-DotNetSdk -GlobalJsonPath (Join-Path $TestDrive 'absent.json') -FallbackVersion '10.0.x' -InstallDir (Join-Path $TestDrive 'd') `
        -TempDir $TestDrive -PathFile '' -EnvFile '' -Download $Download -Install $Install -GetVersion $Version } | Should -Not -Throw
  }
}

Describe 'The default download, install and version commands' {
  It 'downloads with Invoke-WebRequest' {
    Mock Invoke-WebRequest { }
    & (Get-DefaultDownload) 'https://example.invalid/x.ps1' (Join-Path $TestDrive 'x.ps1')
    Should -Invoke Invoke-WebRequest -Times 1 -ParameterFilter { $Uri -eq 'https://example.invalid/x.ps1' -and $OutFile -like '*x.ps1' }
  }

  It 'runs the installer with -Channel and -InstallDir, and fails on its error' {
    $installer = Join-Path $TestDrive 'fake-install.ps1'
    $record = Join-Path $TestDrive 'installer-args.txt'
    Set-Content -LiteralPath $installer -Value "param(`$Channel, `$InstallDir) Set-Content -LiteralPath '$record' -Value `"`$Channel|`$InstallDir`""
    & (Get-DefaultInstall) $installer '10.0.1xx' '/opt/dn'
    Get-Content -LiteralPath $record | Should -Be '10.0.1xx|/opt/dn'
    $throwing = Join-Path $TestDrive 'throwing-install.ps1'
    Set-Content -LiteralPath $throwing -Value 'throw "install failed"'
    { & (Get-DefaultInstall) $throwing '10.0.1xx' '/opt/dn' } | Should -Throw '*install failed*'
  }

  It 'reads the version from the installed dotnet, and throws when it exits non-zero' {
    $dir = Join-Path $TestDrive 'fake-dotnet'
    New-Item -ItemType Directory -Path $dir | Out-Null
    $good = Join-Path $dir 'good.ps1'
    Set-Content -LiteralPath $good -Value "'10.0.100'; exit 0"
    & (Get-DefaultGetVersion) $good | Should -Be '10.0.100'
    $bad = Join-Path $dir 'bad.ps1'
    Set-Content -LiteralPath $bad -Value 'exit 3'
    { & (Get-DefaultGetVersion) $bad } | Should -Throw '*exited 3*'
  }
}
