# Development Environment Setup

## Required Software

### 1. PowerShell 7+
PowerShell Core (cross-platform) is required for all scripts.

**Installation:**
- Windows: `winget install Microsoft.PowerShell`
- macOS: `brew install powershell`
- Linux: https://aka.ms/powershell

**Verify:**
```powershell
pwsh --version
# Should show: PowerShell 7.x or higher
```

### 2. .NET 10 SDK
```bash
# Download from: https://dotnet.microsoft.com/download/dotnet/10.0

# Verify
dotnet --version
# Should show: 10.0.100 or higher
```

### 3. Git
```bash
# Verify
git --version
```

## Optional Setup

### Personal CLAUDE.md Configuration
If using Claude Code, you can optionally create a personal root `CLAUDE.md`
at a location of your choice for:
- Cross-repository navigation
- Personal AI preferences
- Machine-specific settings

**This is completely optional.** The repository's `CLAUDE.md` contains all
project-specific standards and is already configured.

### VSCode Setup (Recommended)

1. **Install VSCode**: https://code.visualstudio.com/

2. **Install Recommended Extensions**
   VSCode will automatically suggest extensions when you open the project.
   See `.vscode/extensions.json` for the list.

   **Key Extensions:**
   - C# Dev Kit
   - SonarLint
   - PowerShell

3. **Verify Setup**
   ```powershell
   pwsh scripts/Run-Tests.ps1
   ```

## Verify Complete Setup

Run all verification steps:

```powershell
# 1. Check PowerShell version
pwsh --version

# 2. Check .NET SDK
dotnet --version

# 3. Restore dependencies
dotnet restore

# 4. Build project
dotnet build

# 5. Run tests
pwsh scripts/Run-Tests.ps1

# 6. Format code
dotnet format --verify-no-changes
```

If all steps complete successfully, you're ready to contribute!

## Next Steps

1. Read [CONTRIBUTING.md](../CONTRIBUTING.md)
2. Review [CLAUDE.md](../CLAUDE.md) for project structure
3. Explore `ai-docs/` for detailed standards
4. Check `plans/` for current initiatives

## Troubleshooting

### PowerShell not found
Make sure PowerShell 7+ is installed and in your PATH.

### .NET 10 SDK not found
Download and install from https://dotnet.microsoft.com/download/dotnet/10.0

### Tests failing
Ensure you've run `dotnet restore` and `dotnet build` first.

## Getting Help

Open an issue or ask in GitHub Discussions if you encounter setup problems.
