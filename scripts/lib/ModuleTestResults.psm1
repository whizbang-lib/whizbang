# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

# Puts every test module's results back beside its own DLL.
#
# Since Testing Platform 2.5, `dotnet test` writes every module's results (TRX, Cobertura, binary coverage, hang
# dumps) to TestResults under its own working directory instead of the module's bin/<config>/<framework>/TestResults.
# Run-Tests.ps1 runs one `dotnet test` from the repository root for a whole suite, so the modules' results would
# share one directory, and every reader (the CI uploads, the coverage gate, the coverage report) looks beside each
# module. So the run asks for a per-module layout under a scratch directory (--results-directory-layout per-module)
# and Move-ModuleTestResult then moves each module's directory into the TestResults beside the module's DLL, keeping
# its inner layout.
#
# The layout is <scratch>/<assembly>/<target framework>_<runtime>, but a module named by --test-modules (a DLL, not a
# project) has no target framework the command knows, so every module's directory is <assembly>/unknown_<runtime>.
# A run of one assembly built for two frameworks (a release branch tests net10.0 and net11.0) therefore writes both
# into one directory; its results are told apart by each TRX (<assembly>_<framework>_<runtime>.trx), which names
# the files it attaches and its deployment directory.

<#
.SYNOPSIS
    The `dotnet test` arguments that write each module's results to its own directory under ResultsRoot.
#>
function Get-ModuleResultsArgument {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)] [string]$ResultsRoot)
    @('--results-directory', $ResultsRoot, '--results-directory-layout', 'per-module')
}

<#
.SYNOPSIS
    Moves each module's results from ResultsRoot (written with Get-ModuleResultsArgument) to the TestResults
    directory beside the module's DLL, and returns the directories it filled.
.DESCRIPTION
    A module's directory is <ResultsRoot>/<assembly name>/<framework or "unknown">_<runtime>. It goes to the one
    module of that assembly name, or, when the run had that assembly for several frameworks, to the module whose
    framework the directory names; failing that, each TRX in it takes itself, the files it attaches and its
    deployment directory to the module of the framework in its name. A file already in the destination with the
    same name is replaced. Whatever belongs to no module (the platform's merged reports, a result no TRX claims)
    moves to the parent of ResultsRoot, where `dotnet test` would have written it, with a warning for a module's
    result that could not be placed. Emptied directories are removed. Safe to call more than once.
#>
function Move-ModuleTestResult {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string]$ResultsRoot,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$ModulePath
    )

    $filled = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) { return }
    $parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ResultsRoot).TrimEnd([char[]]@('/', '\')))

    $modules = @($ModulePath | ForEach-Object {
            $full = [System.IO.Path]::GetFullPath($_)
            [pscustomobject]@{
                Name      = [System.IO.Path]::GetFileNameWithoutExtension($full)
                Framework = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($full))
                Results   = Join-Path ([System.IO.Path]::GetDirectoryName($full)) 'TestResults'
            }
        })

    foreach ($project in @(Get-ChildItem -LiteralPath $ResultsRoot -Directory)) {
        $candidates = @($modules | Where-Object Name -EQ $project.Name)
        if ($candidates.Count -eq 0) {
            # Not a module's: the platform's merged reports.
            Move-DirectoryContent -Source $project.FullName -Destination (Join-Path $parent $project.Name)
            continue
        }
        foreach ($target in @(Get-ChildItem -LiteralPath $project.FullName -Directory)) {
            $named = @($candidates | Where-Object {
                    $target.Name -eq $_.Framework -or $target.Name.StartsWith("$($_.Framework)_", [System.StringComparison]::Ordinal)
                })
            $module = if ($candidates.Count -eq 1) { $candidates[0] } elseif ($named.Count -eq 1) { $named[0] } else { $null }
            if ($module) {
                Move-DirectoryContent -Source $target.FullName -Destination $module.Results
                if (-not $filled.Contains($module.Results)) { $filled.Add($module.Results) }
                continue
            }
            foreach ($results in (Split-ByTrx -Directory $target.FullName -Module $candidates)) {
                if (-not $filled.Contains($results)) { $filled.Add($results) }
            }
            if (Get-ChildItem -LiteralPath $target.FullName -File -Recurse -Force) {
                Write-Warning ("Test results in $($target.FullName) belong to more than one build of $($project.Name) " +
                    "and no TRX names them; moved to $parent.")
                Move-DirectoryContent -Source $target.FullName -Destination $parent
            } else {
                Remove-Item -LiteralPath $target.FullName -Recurse -Force
            }
        }
        Remove-EmptyDirectory $project.FullName
    }

    # Anything the platform wrote outside a module's directory goes where `dotnet test` would have put it.
    foreach ($file in @(Get-ChildItem -LiteralPath $ResultsRoot -File)) {
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $parent $file.Name) -Force
    }
    Remove-EmptyDirectory $ResultsRoot
    $filled
}

# Moves each TRX in Directory, with the files it attaches and its deployment directory, to the module of the framework
# in its name, and returns the directories filled. What no TRX claims stays in Directory.
function Split-ByTrx([string]$Directory, [object[]]$Module) {
    foreach ($trx in @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File)) {
        # Not $module: PowerShell names are case-insensitive, and that would overwrite $Module.
        $owner = @($Module | Where-Object { $trx.Name.StartsWith("$($_.Name)_$($_.Framework)_", [System.StringComparison]::Ordinal) })
        if ($owner.Count -ne 1) { continue }
        $destination = $owner[0].Results
        $text = Get-Content -LiteralPath $trx.FullName -Raw
        $claimed = [System.Collections.Generic.List[string]]::new()
        foreach ($m in [regex]::Matches($text, '<A\s+href="([^"]+)"')) { $claimed.Add([System.IO.Path]::GetFileName($m.Groups[1].Value)) }
        $deployment = [regex]::Match($text, 'runDeploymentRoot="([^"]+)"')
        if ($deployment.Success) {
            $root = Join-Path $Directory $deployment.Groups[1].Value
            if (Test-Path -LiteralPath $root -PathType Container) {
                Move-DirectoryContent -Source $root -Destination (Join-Path $destination $deployment.Groups[1].Value)
            }
        }
        foreach ($name in $claimed) {
            $file = Join-Path $Directory $name
            if (Test-Path -LiteralPath $file -PathType Leaf) { Move-FileTo $file (Join-Path $destination $name) }
        }
        Move-FileTo $trx.FullName (Join-Path $destination $trx.Name)
        $destination
    }
}

function Move-FileTo([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($Destination)) -Force | Out-Null
    Move-Item -LiteralPath $Source -Destination $Destination -Force
}

# Moves every file under Source to the same relative path under Destination, then removes what is left of Source.
function Move-DirectoryContent([string]$Source, [string]$Destination) {
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)) {
        Move-FileTo $file.FullName (Join-Path $Destination ([System.IO.Path]::GetRelativePath($Source, $file.FullName)))
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Remove-Item -LiteralPath $Source -Recurse -Force
}

function Remove-EmptyDirectory([string]$Path) {
    if ((Test-Path -LiteralPath $Path -PathType Container) -and -not (Get-ChildItem -LiteralPath $Path -Force)) {
        Remove-Item -LiteralPath $Path -Force
    }
}

Export-ModuleMember -Function Get-ModuleResultsArgument, Move-ModuleTestResult
