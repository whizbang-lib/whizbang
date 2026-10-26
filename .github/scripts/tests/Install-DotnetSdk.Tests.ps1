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

Describe 'Invoke-SdkInstall' {
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
