# Third-Party Services

This document lists all third-party services used by the Whizbang project.

## Code Quality

### SonarCloud
- **Purpose**: Static code analysis, code quality, and security scanning
- **Free for Open Source**: Yes (unlimited public projects)
- **Dashboard**: https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang
- **Integration**: Automated via GitHub Actions (`.github/workflows/quality.yml`)
- **Quality Gate**: 100% coverage, A rating, 0 bugs, 0 vulnerabilities
- **Configuration**: `sonar-project.properties`

**Setup**:
1. Sign in at https://sonarcloud.io with GitHub
2. Import repository `whizbang-lib/whizbang`
3. Add `SONAR_TOKEN` to GitHub repository secrets
4. Quality checks run automatically on every PR and push

## Test Coverage

### Codecov
- **Purpose**: Test coverage tracking and reporting
- **Free for Open Source**: Yes
- **Dashboard**: https://codecov.io/gh/whizbang-lib/whizbang
- **Integration**: Automated via GitHub Actions (`.github/workflows/test.yml`)
- **Requirement**: 100% test coverage
- **Configuration**: `codecov.yml`

**Setup**:
1. Sign in at https://codecov.io with GitHub
2. Add repository `whizbang-lib/whizbang`
3. Add `CODECOV_TOKEN` to GitHub repository secrets
4. Coverage reports upload automatically on test runs

## Security

### Dependabot
- **Purpose**: Automated dependency updates and security alerts
- **Free for Public Repos**: Yes (GitHub built-in)
- **Integration**: Configured via `.github/dependabot.yml`
- **Scan Frequency**: Weekly (Mondays)

**What it scans**:
- NuGet packages
- GitHub Actions

### GitHub CodeQL
- **Purpose**: Advanced security scanning and vulnerability detection
- **Free for Public Repos**: Yes (GitHub built-in)
- **Integration**: Automated via `.github/workflows/codeql.yml`
- **Scan Frequency**:
  - Every push to main/develop
  - Every pull request
  - Weekly scheduled scan (Sundays)

**Languages**: C#

### GitHub Secret Scanning
- **Purpose**: Detect accidentally committed secrets (API keys, tokens, etc.)
- **Free for Public Repos**: Yes (GitHub built-in)
- **Integration**: Automatically enabled for public repositories

## CI/CD

### GitHub Actions
- **Purpose**: Continuous integration, testing, and deployment
- **Free for Public Repos**: Yes (unlimited minutes)
- **Workflows**:
  - `build.yml` - Build and format verification
  - `test.yml` - Tests with coverage reporting
  - `quality.yml` - SonarCloud analysis
  - `nuget-pack.yml` - Package validation
  - `nuget-publish.yml` - Automated NuGet publishing
  - `codeql.yml` - Security scanning

## Package Distribution

### NuGet.org
- **Purpose**: .NET package distribution
- **Free**: Yes
- **Organization**: `whizbang-lib`
- **Packages**: https://www.nuget.org/packages?q=whizbang
- **Integration**: Automated publishing via GitHub Actions

## Documentation

### GitHub Pages
- **Purpose**: Documentation website hosting
- **Free**: Yes (GitHub built-in)
- **URL**: https://whizbang-lib.github.io
- **Repository**: `whizbang-lib/whizbang-lib.github.io`
- **Technology**: Angular 20 with custom search

## Developer Tools

### VSCode Extensions
Recommended extensions configured in `.vscode/extensions.json`:
- SonarLint (code quality in IDE)
- C# Dev Kit
- PowerShell

## Service Tokens

The following secrets must be configured in GitHub repository settings:

- `SONAR_TOKEN` - SonarCloud authentication token
- `CODECOV_TOKEN` - Codecov upload token
- `NUGET_API_KEY` - NuGet.org API key for package publishing

## Service Status

All services are monitored via their respective dashboards. Check status pages:
- GitHub: https://www.githubstatus.com/
- NuGet: https://status.nuget.org/
- SonarCloud: https://status.sonarcloud.io/
- Codecov: https://status.codecov.io/

## Contact

For service access or configuration issues, contact the repository maintainers.
