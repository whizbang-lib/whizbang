#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Kills all running Whizbang processes and frees up ports.

.DESCRIPTION
    Cross-platform script to terminate all Whizbang-related processes and containers including:
    - .NET processes running Whizbang examples/tests
    - Docker containers with whiz-example- and whiz-test- prefixes
    - Docker volumes with whiz-example- and whiz-test- prefixes

    Works on Windows, macOS, and Linux.

.PARAMETER Force
    Skip confirmation prompt.

.PARAMETER DryRun
    Preview what would be killed without making changes.

.PARAMETER IncludeDocker
    Also stop and remove Whizbang Docker containers and volumes.

.EXAMPLE
    .\scripts\kill-whizbang.ps1
    # Interactive kill with confirmation

.EXAMPLE
    .\scripts\kill-whizbang.ps1 -Force -IncludeDocker
    # Kill everything without confirmation

.NOTES
    Use this when you need to stop all Whizbang sample/test processes and containers.
#>

param(
    [switch]$Force,
    [switch]$DryRun,
    [switch]$IncludeDocker
)

$ErrorActionPreference = "Stop"

# Whizbang process names to look for
$processNames = @(
    "ECommerce.AppHost",
    "ECommerce.OrderService.API",
    "ECommerce.BFF.API",
    "ECommerce.InventoryWorker",
    "ECommerce.PaymentWorker",
    "ECommerce.ShippingWorker",
    "ECommerce.NotificationWorker",
    "ECommerce.AzureServiceBus.Integration.Tests.AppHost"
)

$killedProcesses = @()
$killedContainers = @()
$killedVolumes = @()
$errors = @()

#region Helper Functions

function Write-Header {
    param([string]$Text)
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step {
    param([string]$Text)
    Write-Host "🔹 $Text" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Text)
    Write-Host "✅ $Text" -ForegroundColor Green
}

function Write-Warning {
    param([string]$Text)
    Write-Host "⚠️  $Text" -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Text)
    Write-Host "❌ $Text" -ForegroundColor Red
}

function Write-Item {
    param([string]$Text)
    Write-Host "   • $Text" -ForegroundColor Yellow
}

#endregion

#region Process Discovery

function Get-WhizbangProcesses {
    Write-Step "Discovering Whizbang processes..."

    $processes = @()

    # Get all dotnet processes
    $dotnetProcesses = Get-Process -Name dotnet -ErrorAction SilentlyContinue

    foreach ($proc in $dotnetProcesses) {
        try {
            # Get command line - platform-specific
            $cmdLine = ""
            if ($IsWindows -or $PSVersionTable.PSVersion.Major -lt 6) {
                # Windows
                $cmdLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue).CommandLine
            } else {
                # macOS/Linux
                if ($IsMacOS) {
                    $cmdLine = (ps -p $proc.Id -o command= 2>$null)
                } else {
                    $cmdLine = (cat "/proc/$($proc.Id)/cmdline" 2>$null) -replace "`0", " "
                }
            }

            # Check if command line contains any Whizbang process name
            foreach ($processName in $processNames) {
                if ($cmdLine -like "*$processName*") {
                    $processes += [PSCustomObject]@{
                        Id = $proc.Id
                        Name = $proc.Name
                        Service = $processName
                        CommandLine = $cmdLine
                    }
                    break
                }
            }
        } catch {
            # Ignore errors for individual processes (may have terminated)
        }
    }

    return $processes | Select-Object -Unique -Property Id, Name, Service, CommandLine
}

function Get-WhizbangDockerContainers {
    Write-Step "Discovering Whizbang Docker containers..."

    $containers = @()

    try {
        # Get all containers with Whizbang prefixes: 'whiz-example-', 'whiz-test-'
        $prefixes = @("whiz-example-", "whiz-test-")

        foreach ($prefix in $prefixes) {
            $dockerOutput = docker ps -a --filter "name=$prefix" --format "{{.ID}}`t{{.Names}}`t{{.Status}}" 2>$null
            if ($dockerOutput) {
                foreach ($line in $dockerOutput) {
                    $parts = $line -split "`t"
                    if ($parts.Count -ge 3) {
                        $containers += [PSCustomObject]@{
                            Id = $parts[0]
                            Name = $parts[1]
                            Status = $parts[2]
                        }
                    }
                }
            }
        }
    } catch {
        # Ignore errors (Docker may not be running)
    }

    return $containers | Select-Object -Unique -Property Id, Name, Status
}

function Get-WhizbangDockerVolumes {
    Write-Step "Discovering Whizbang Docker volumes..."

    $volumes = @()

    try {
        # Get all volumes with Whizbang prefixes: 'whiz-example-', 'whiz-test-'
        $prefixes = @("whiz-example-", "whiz-test-")

        foreach ($prefix in $prefixes) {
            $dockerOutput = docker volume ls --filter "name=$prefix" --format "{{.Name}}" 2>$null
            if ($dockerOutput) {
                foreach ($volumeName in $dockerOutput) {
                    if ($volumeName) {
                        $volumes += [PSCustomObject]@{
                            Name = $volumeName
                        }
                    }
                }
            }
        }
    } catch {
        # Ignore errors (Docker may not be running)
    }

    return $volumes | Select-Object -Unique -Property Name
}

#endregion

#region Cleanup Functions

function Stop-WhizbangProcesses {
    param(
        [PSCustomObject[]]$Processes
    )

    foreach ($proc in $Processes) {
        $description = "$($proc.Service) (PID: $($proc.Id))"

        if ($DryRun) {
            Write-Item "[DRY RUN] Would kill: $description"
        } else {
            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                Write-Item "Killed: $description"
                $script:killedProcesses += $description
            } catch {
                $errorMsg = "Failed to kill $description : $_"
                $script:errors += $errorMsg
                Write-Error $errorMsg
            }
        }
    }
}

function Stop-WhizbangDockerContainers {
    param([PSCustomObject[]]$Containers)

    foreach ($container in $Containers) {
        $isRunning = $container.Status -like "Up*"

        if ($DryRun) {
            Write-Item "[DRY RUN] Would stop container: $($container.Name) ($($container.Id.Substring(0,12)))"
        } else {
            try {
                if ($isRunning) {
                    docker stop $container.Id 2>$null | Out-Null
                }
                docker rm $container.Id 2>$null | Out-Null
                Write-Item "Stopped and removed: $($container.Name)"
                $script:killedContainers += $container.Name
            } catch {
                $errorMsg = "Failed to stop container $($container.Name): $_"
                $script:errors += $errorMsg
                Write-Error $errorMsg
            }
        }
    }
}

function Remove-WhizbangDockerVolumes {
    param([PSCustomObject[]]$Volumes)

    foreach ($volume in $Volumes) {
        if ($DryRun) {
            Write-Item "[DRY RUN] Would remove volume: $($volume.Name)"
        } else {
            try {
                docker volume rm $volume.Name 2>$null | Out-Null
                Write-Item "Removed: $($volume.Name)"
                $script:killedVolumes += $volume.Name
            } catch {
                $errorMsg = "Failed to remove volume $($volume.Name): $_"
                $script:errors += $errorMsg
                Write-Error $errorMsg
            }
        }
    }
}

#endregion

#region Main Execution

# Display banner
Write-Host ""
Write-Host "⚡ Whizbang Process Killer" -ForegroundColor Magenta
Write-Host "===========================" -ForegroundColor Magenta
if ($DryRun) {
    Write-Host "   [DRY RUN MODE - No processes or containers will be killed]" -ForegroundColor Yellow
}
Write-Host ""

# Discover processes
Write-Header "DISCOVERING PROCESSES"

$whizbangProcesses = Get-WhizbangProcesses
$dockerContainers = @()
$dockerVolumes = @()

if ($IncludeDocker) {
    $dockerContainers = Get-WhizbangDockerContainers
    $dockerVolumes = Get-WhizbangDockerVolumes
}

# Display what will be killed
Write-Header "PROCESSES TO KILL"

if ($whizbangProcesses.Count -gt 0) {
    Write-Host "🔧 Whizbang Processes:" -ForegroundColor Cyan
    foreach ($proc in $whizbangProcesses) {
        Write-Host "   • $($proc.Service) (PID: $($proc.Id))" -ForegroundColor Yellow
    }
    Write-Host ""
} else {
    Write-Host "   No Whizbang processes found" -ForegroundColor DarkGray
    Write-Host ""
}

if ($IncludeDocker) {
    if ($dockerContainers.Count -gt 0) {
        Write-Host "🐳 Docker Containers:" -ForegroundColor Cyan
        foreach ($container in $dockerContainers) {
            Write-Host "   • $($container.Name) ($($container.Status))" -ForegroundColor Yellow
        }
        Write-Host ""
    } else {
        Write-Host "   No Whizbang Docker containers found" -ForegroundColor DarkGray
        Write-Host ""
    }

    if ($dockerVolumes.Count -gt 0) {
        Write-Host "💾 Docker Volumes:" -ForegroundColor Cyan
        foreach ($volume in $dockerVolumes) {
            Write-Host "   • $($volume.Name)" -ForegroundColor Yellow
        }
        Write-Host ""
    } else {
        Write-Host "   No Whizbang Docker volumes found" -ForegroundColor DarkGray
        Write-Host ""
    }
}

# Check if there's anything to kill
if ($whizbangProcesses.Count -eq 0 -and $dockerContainers.Count -eq 0 -and $dockerVolumes.Count -eq 0) {
    Write-Success "No Whizbang processes, containers, or volumes found!"
    exit 0
}

# Confirmation
if (-not $Force -and -not $DryRun) {
    Write-Host ""
    $totalItems = $whizbangProcesses.Count + $dockerContainers.Count + $dockerVolumes.Count
    $itemParts = @()
    if ($whizbangProcesses.Count -gt 0) {
        $itemParts += "$($whizbangProcesses.Count) process(es)"
    }
    if ($dockerContainers.Count -gt 0) {
        $itemParts += "$($dockerContainers.Count) container(s)"
    }
    if ($dockerVolumes.Count -gt 0) {
        $itemParts += "$($dockerVolumes.Count) volume(s)"
    }
    $itemsText = $itemParts -join ", "
    Write-Host "⚠️  About to kill $itemsText" -ForegroundColor Yellow
    Write-Host ""

    $confirm = Read-Host "Are you sure you want to proceed? (y/N)"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# Kill processes
Write-Header "KILLING PROCESSES"

if ($whizbangProcesses.Count -gt 0) {
    Write-Host "⚡ Killing Whizbang processes..." -ForegroundColor Cyan
    Stop-WhizbangProcesses -Processes $whizbangProcesses
    Write-Host ""
}

if ($IncludeDocker -and $dockerContainers.Count -gt 0) {
    Write-Host "⚡ Stopping and removing Docker containers..." -ForegroundColor Cyan
    Stop-WhizbangDockerContainers -Containers $dockerContainers
    Write-Host ""
}

if ($IncludeDocker -and $dockerVolumes.Count -gt 0) {
    Write-Host "⚡ Removing Docker volumes..." -ForegroundColor Cyan
    Remove-WhizbangDockerVolumes -Volumes $dockerVolumes
    Write-Host ""
}

# Summary
Write-Header "SUMMARY"

if ($DryRun) {
    Write-Success "Dry run completed - no processes, containers, or volumes were killed"
    Write-Host "   Run without -DryRun to kill processes/containers/volumes" -ForegroundColor DarkGray
} else {
    if ($killedProcesses.Count -gt 0) {
        Write-Success "Killed $($killedProcesses.Count) process(es)"
    }

    if ($killedContainers.Count -gt 0) {
        Write-Success "Stopped and removed $($killedContainers.Count) container(s)"
    }

    if ($killedVolumes.Count -gt 0) {
        Write-Success "Removed $($killedVolumes.Count) volume(s)"
    }

    if ($errors.Count -gt 0) {
        Write-Host ""
        Write-Warning "Completed with $($errors.Count) error(s)"
        foreach ($error in $errors) {
            Write-Host "   • $error" -ForegroundColor Red
        }
        exit 1
    } else {
        Write-Host ""
        $messageParts = @("All Whizbang")
        if ($killedProcesses.Count -gt 0) {
            $messageParts += "processes"
        }
        if ($killedContainers.Count -gt 0) {
            $messageParts += "containers"
        }
        if ($killedVolumes.Count -gt 0) {
            $messageParts += "volumes"
        }
        $message = ($messageParts -join ", ") + " terminated successfully!"
        $message = $message -replace ", ([^,]*)$", " and `$1"  # Replace last comma with "and"
        Write-Success $message
    }
}

#endregion
