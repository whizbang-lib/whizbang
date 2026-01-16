#
# Clean ILRepack file locks from lingering Aspire/ECommerce processes (PowerShell version)
#
# This script fixes the recurring ILRepack error:
#   "The file '.../Whizbang.Generators.dll' already exists"
#
# Root cause: Aspire doesn't always terminate child processes when debugging stops,
# leaving file locks on generator DLLs.
#
# Usage: .\clean-generator-locks.ps1
#

$ErrorActionPreference = "Continue"  # Continue on errors (processes might not exist)

Write-Host "==========================================="
Write-Host "Cleaning Whizbang Generator File Locks"
Write-Host "==========================================="
Write-Host ""

# Kill ECommerce processes (main source of locks)
Write-Host "[1/5] Killing ECommerce processes..."
try {
    Get-Process | Where-Object {$_.ProcessName -like "*ECommerce*"} | Stop-Process -Force
    Write-Host "  ✓ Killed ECommerce processes"
} catch {
    Write-Host "  No ECommerce processes found"
}

# Kill any lingering Aspire AppHost processes
Write-Host "[2/5] Killing Aspire AppHost processes..."
try {
    Get-Process | Where-Object {$_.ProcessName -like "*AppHost*"} | Stop-Process -Force
    Write-Host "  ✓ Killed AppHost processes"
} catch {
    Write-Host "  No AppHost processes found"
}

# Shut down MSBuild build server (may hold locks)
Write-Host "[3/5] Shutting down dotnet build server..."
dotnet build-server shutdown

# Clean build artifacts
Write-Host "[4/5] Cleaning generator build artifacts..."
dotnet clean src\Whizbang.Generators\Whizbang.Generators.csproj --verbosity minimal
dotnet clean src\Whizbang.Data.EFCore.Postgres.Generators\Whizbang.Data.EFCore.Postgres.Generators.csproj --verbosity minimal

# Remove locked files directly
Write-Host "[5/5] Removing locked DLL/PDB files..."
Remove-Item -Path "src\Whizbang.Generators\bin\Debug\netstandard2.0\Whizbang.Generators.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "src\Whizbang.Generators\bin\Debug\netstandard2.0\Whizbang.Generators.pdb" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "src\Whizbang.Data.EFCore.Postgres.Generators\bin\Debug\netstandard2.0\Whizbang.Data.EFCore.Postgres.Generators.dll" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "src\Whizbang.Data.EFCore.Postgres.Generators\bin\Debug\netstandard2.0\Whizbang.Data.EFCore.Postgres.Generators.pdb" -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "✓ File locks cleaned successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "You can now run: dotnet build"
Write-Host "==========================================="
