# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Install-DotnetSdk.ps1')

  # A stand-in for dotnet-install.sh: exits with $ExitCode, and on success leaves a dotnet that prints a version
  # and exits with $DotnetExitCode. Written with LF so bash runs it on every platform.
  function New-FakeInstaller {
    param([int]$ExitCode = 0, [switch]$NoDotnet, [int]$DotnetExitCode = 0, [switch]$Chatty)
    $path = Join-Path $TestDrive "dotnet-install-$([guid]::NewGuid().ToString('N')).sh"
    $lines = @('#!/usr/bin/env bash', 'while [ $# -gt 0 ]; do case "$1" in --install-dir) dir="$2"; shift 2;; *) shift;; esac; done')
    if ($Chatty) {
      # The real dotnet-install.sh prints its progress to stdout.
      $lines += 'echo "dotnet-install: Attempting to download using aka.ms link"'
      $lines += 'echo "dotnet-install: Installed version is 10.0.999"'
    }
    if ($ExitCode -ne 0) {
      $lines += 'echo "dotnet-install: Failed to download: 409" >&2'
      $lines += "exit $ExitCode"
    }
    elseif (-not $NoDotnet) {
      $lines += 'mkdir -p "$dir"'
      $lines += "printf '#!/usr/bin/env bash\necho 10.0.999\nexit $DotnetExitCode\n' > `"`$dir/dotnet`""
      $lines += 'chmod +x "$dir/dotnet"'
    }
    $lines += 'exit 0'
    [IO.File]::WriteAllText($path, ($lines -join "`n") + "`n")
    return $path
  }
}

Describe 'Get-SdkChannel' {
  It 'installs the feature band of the global.json SDK version' {
    $json = Join-Path $TestDrive 'global.json'
    '{"sdk":{"version":"10.0.100","rollForward":"latestMajor"}}' | Set-Content -Path $json
    Get-SdkChannel -GlobalJsonPath $json -FallbackChannel '9.0.x' | Should -Be '10.0.1xx'
  }

  It 'falls back to the input channel, without its trailing .x, when there is no global.json' {
    Get-SdkChannel -GlobalJsonPath (Join-Path $TestDrive 'missing.json') -FallbackChannel '10.0.x' | Should -Be '10.0'
  }

  It 'falls back to the input channel when global.json names no SDK version' {
    $json = Join-Path $TestDrive 'nosdk.json'
    '{"test":{"runner":"Microsoft.Testing.Platform"}}' | Set-Content -Path $json
    Get-SdkChannel -GlobalJsonPath $json -FallbackChannel '9.0' | Should -Be '9.0'
  }
}

# This script runs only on Linux and macOS (setup-dotnet-retry sends Windows to Install-DotNetSdkWindows.ps1), and these
# cases run bash, which a Windows runner lacks or has only as the WSL stub.
Describe 'Invoke-SdkInstall' -Skip:$IsWindows {
  It 'reports success with the installed version when the installer and dotnet both succeed' {
    $dir = Join-Path $TestDrive 'ok'
    $r = Invoke-SdkInstall -InstallerPath (New-FakeInstaller) -Channel '10.0.1xx' -InstallDir $dir
    $r.Ok | Should -BeTrue
    $r.Version | Should -Be '10.0.999'
  }

  It 'returns one result, not the installer''s progress lines, when the installer writes to stdout' {
    $dir = Join-Path $TestDrive 'chatty'
    $r = Invoke-SdkInstall -InstallerPath (New-FakeInstaller -Chatty) -Channel '10.0.1xx' -InstallDir $dir
    @($r).Count | Should -Be 1
    $r.Ok | Should -BeTrue
    $r.Version | Should -Be '10.0.999'
  }

  It 'names the channel and the installer exit code when the installer fails, instead of running dotnet' {
    $dir = Join-Path $TestDrive 'fail'
    $r = Invoke-SdkInstall -InstallerPath (New-FakeInstaller -ExitCode 1) -Channel '10.0.1xx' -InstallDir $dir
    $r.Ok | Should -BeFalse
    $r.Error | Should -BeLike '*10.0.1xx*'
    $r.Error | Should -BeLike '*exit 1*'
    $r.Error | Should -BeLike '*download errors above*'
  }

  It 'says no SDK was installed when the installer exits 0 but leaves no dotnet' {
    $dir = Join-Path $TestDrive 'empty'
    $r = Invoke-SdkInstall -InstallerPath (New-FakeInstaller -NoDotnet) -Channel '10.0.1xx' -InstallDir $dir
    $r.Ok | Should -BeFalse
    $r.Error | Should -BeLike "*no SDK at*$dir*"
  }

  It 'reports a dotnet that is present but fails to run' {
    $dir = Join-Path $TestDrive 'broken'
    $r = Invoke-SdkInstall -InstallerPath (New-FakeInstaller -DotnetExitCode 3) -Channel '10.0.1xx' -InstallDir $dir
    $r.Ok | Should -BeFalse
    $r.Error | Should -BeLike '*dotnet --version*exit 3*'
  }
}

Describe 'Get-SdkChannel with a prerelease SDK (#2135)' {
  It 'installs the exact version when global.json names a prerelease SDK, which has no feature-band channel' {
    $json = Join-Path $TestDrive 'prerelease.json'
    '{"sdk":{"version":"11.0.100-rc.3.26522.148","allowPrerelease":true}}' | Set-Content -Path $json
    Get-SdkChannel -GlobalJsonPath $json -FallbackChannel '10.0.x' | Should -Be '11.0.100-rc.3.26522.148'
  }
}

Describe 'Get-ExtraRuntimeChannels (#2135)' {
  It 'asks for the .NET 10 runtime when a release build targets net10.0 and net11.0 on SDK 11' {
    Get-ExtraRuntimeChannels -TargetFrameworks @('net10.0', 'net11.0') -SdkVersion '11.0.100-rc.3.26522.148' | Should -Be @('10.0')
  }

  It 'asks for nothing when the build targets only the SDK''s own framework' {
    @(Get-ExtraRuntimeChannels -TargetFrameworks @('net11.0') -SdkVersion '11.0.100').Count | Should -Be 0
  }

  It 'ignores netstandard and empty entries' {
    @(Get-ExtraRuntimeChannels -TargetFrameworks @('netstandard2.0', '') -SdkVersion '11.0.100').Count | Should -Be 0
  }
}

Describe 'Invoke-SdkInstall with an exact version (#2135)' -Skip:$IsWindows {
  It 'passes --version, not --channel, for a prerelease SDK version' {
    $log = Join-Path $TestDrive 'args.txt'
    $path = Join-Path $TestDrive 'record-installer.sh'
    $lines = @('#!/usr/bin/env bash', "echo `"`$@`" > '$log'",
      'while [ $# -gt 0 ]; do case "$1" in --install-dir) dir="$2"; shift 2;; *) shift;; esac; done',
      'mkdir -p "$dir"', "printf '#!/usr/bin/env bash\necho 11.0.100-rc.3\n' > `"`$dir/dotnet`"", 'chmod +x "$dir/dotnet"', 'exit 0')
    [IO.File]::WriteAllText($path, ($lines -join "`n") + "`n")
    $r = Invoke-SdkInstall -InstallerPath $path -Channel '11.0.100-rc.3.26522.148' -InstallDir (Join-Path $TestDrive 'exact')
    $r.Ok | Should -BeTrue
    (Get-Content $log -Raw) | Should -BeLike '--version 11.0.100-rc.3.26522.148 --install-dir*'
  }
}
