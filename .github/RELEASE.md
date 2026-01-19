# Whizbang v0.1.0 Release Plan

**Status:** In Progress - Alpha Phase
**Current Version:** v0.1.0-alpha
**Target Coverage:** 100%
**Quality Standard:** Zero errors, zero warnings
**Created:** 2024-12-22
**Last Updated:** 2024-12-22 (Added warning suppression audit & release wizard specification)

---

## Status Tracking

### Alpha Phase Progress: 0/11 sections complete
- [ ] 1. Repository Hygiene & Organization
- [ ] 2. Build Quality - Zero Errors/Warnings
- [ ] 3. Documentation & Standards Verification
- [ ] 4. CLAUDE.md Organization Strategy
- [ ] 5. Build & CI/CD Infrastructure
- [ ] 6. NuGet Package Configuration
- [ ] 7. Quality Assurance (Testing & Code Quality)
- [ ] 8. Documentation Publishing (Library)
- [ ] 9. Open Source Services Setup
- [ ] 10. Legal & Compliance
- [ ] 11. Release Process Documentation

### Beta Phase Progress: 0/5 sections complete
- [ ] Update README with badges and installation
- [ ] CLI Documentation
- [ ] Performance Testing
- [ ] Documentation Site Publishing
- [ ] VSCode Extension Updates

### GA Phase Progress: 0/2 sections complete
- [ ] Blog Section
- [ ] Community Announcements

---

## Release Philosophy: Three-Phase Approach

### Phase 1: Alpha (Internal Testing) ← **WE ARE HERE**
- **Focus:** Core functionality verification
- **Audience:** Internal/maintainers only
- **NuGet:** Publish with `-alpha` suffix (e.g., v0.1.0-alpha.1)
- **Activities:** Testing, documentation verification, infrastructure setup
- **Exit Criteria:**
  - 0 errors, 0 warnings in build
  - 100% test coverage
  - All CI/CD pipelines passing
  - Internal testing complete
  - All infrastructure configured

### Phase 2: Beta (Limited Public Testing)
- **Focus:** Real-world validation
- **Audience:** Early adopters, select community members
- **NuGet:** Publish with `-beta` suffix (e.g., v0.1.0-beta.1)
- **Activities:** Gather feedback, fix issues, performance validation
- **Exit Criteria:**
  - Beta feedback addressed
  - Performance validated
  - Documentation complete and published
  - CLI documentation ready
  - VSCode extension compatible

### Phase 3: GA (General Availability)
- **Focus:** Public release
- **Audience:** General .NET community
- **NuGet:** Publish as v0.1.0 (stable)
- **Activities:** Community announcements, blog posts, social media
- **Exit Criteria:**
  - All critical issues resolved
  - Blog post published
  - Community announcements sent
  - Release notes finalized

---

## Third-Party Services - Final Selection

### ✅ Confirmed Free for Open Source

**SonarCloud**
- **Status:** RECOMMENDED
- **Cost:** FREE for public/open source projects (unlimited)
- **Limits:** Private projects limited to 50K LOC on free tier
- **Features:** Full features for public repos
- **Decision:** Use SonarCloud for code quality analysis

**GitHub Actions**
- **Status:** CONFIRMED
- **Cost:** FREE for public repositories
- **Limits:** None for public repos
- **Decision:** Primary CI/CD platform

**Codecov**
- **Status:** CONFIRMED
- **Cost:** FREE for open source
- **Decision:** Use for test coverage tracking

**Dependabot**
- **Status:** CONFIRMED
- **Cost:** FREE (GitHub-native)
- **Decision:** Use for dependency updates

**GitHub Advanced Security**
- **Status:** CONFIRMED
- **Cost:** FREE for public repositories
- **Decision:** Use for vulnerability scanning

**Shields.io**
- **Status:** CONFIRMED
- **Cost:** Completely FREE (serves 1.6B+ images/month)
- **Details:** Open source project, community-supported
- **Decision:** Use for README badges

**NuGet.org**
- **Status:** CONFIRMED
- **Cost:** FREE package hosting
- **Decision:** Primary package distribution

**GitHub Pages**
- **Status:** CONFIRMED
- **Cost:** FREE for public repositories
- **Decision:** Documentation site + blog hosting

### ❌ Not Recommended

**Snyk**
- **Status:** NOT RECOMMENDED
- **Reasons:**
  - Limited free tier: 400 tests/month for Open Source scanning
  - Scope reduced to "1-2 code bases"
  - Missing features in free tier: reporting, SSO, advanced scanning
  - Weekly-only recurring scans for private repos
- **Alternative:** Use Dependabot + GitHub Advanced Security instead

**CircleCI**
- **Status:** NOT NEEDED
- **Reason:** GitHub Actions provides better integration and is free for public repos
- **Decision:** Use GitHub Actions only

---

## 1. Repository Hygiene & Organization (Alpha)

### 1.1 Clean Root Directory

**Development Artifacts to Remove:**
- [ ] `.DS_Store`
- [ ] `.gitignore.bak`
- [ ] `baseline-test-results.txt`
- [ ] `coverage.xml`
- [ ] `coverage_files.txt`
- [ ] `merged_coverage.cobertura.xml`
- [ ] `test-results.txt`

**Temporary Markdown Files to Remove:**
- [ ] `perspective-implementation-summary.md`
- [ ] `work-coordinator-refactor-progress.md`
- [ ] `process-work-batch-flow.md`
- [ ] `NEXT_SESSION.md`
- [ ] `SETUP_SUMMARY.md`
- [ ] `TESTING.md` (consolidate into docs site)
- [ ] `contributing-database-generators.md` (move to docs site or ai-docs)

**Python Scripts to Remove (one-off usage):**
- [ ] `analyze_all_coverage.py`
- [ ] `analyze_coverage.py`
- [ ] `fix_benchmarks.py`
- [ ] `fix_hop_assertions.py`
- [ ] `fix_hops.py`
- [ ] `fix_message_hops.py`
- [ ] `show_core_coverage_details.py`

**Bash Scripts to Remove (convert needed ones to PowerShell):**
- [ ] `collect_coverage.sh`
- [ ] `merge_coverage.sh`
- [ ] `run_all_tests_coverage.sh`
- [ ] `run_all_tests_with_coverage.sh`
- [ ] `run-tests.sh`
- [ ] `fix_messagehops.sh`

**Files to Move:**
- [ ] `whizbang-postgres-schema.sql` → Move to `samples/ECommerce/schema/` or `docs/`

**Directories to Clean:**
- [ ] `TestResults/` - Clear contents, verify in .gitignore

**Update .gitignore:**
```gitignore
# Development artifacts
.DS_Store
*.bak
TestResults/
merged_coverage*.xml
coverage.xml
coverage_files.txt
baseline-test-results.txt
test-results.txt

# Generator outputs
**/.whizbang-generated/

# Temporary files
*.tmp
```

### 1.2 Reorganize Scripts Directory

**Audit PowerShell Scripts:**
- [ ] Review `run-all-tests.ps1` vs `run-tests-summary.ps1` - Deduplicate
- [ ] Review `clean-generator-locks.ps1` - Keep if needed ongoing
- [ ] Convert any needed bash functionality to PowerShell Core 7+
- [ ] Move all scripts to `scripts/` directory
- [ ] Organize by purpose (dev, ci, tools)
- [ ] Delete redundant scripts

**Create scripts/README.md:**
```markdown
# Whizbang Scripts

All scripts require **PowerShell 7+** (cross-platform).

## Development Scripts
- **Run-Tests.ps1** - Run all tests with optional filtering
- **Clean-GeneratorLocks.ps1** - Clean source generator lock files
- [Other scripts with descriptions]

## CI/CD Scripts
- [Scripts used by GitHub Actions]

## Installation
Install PowerShell 7+: https://aka.ms/powershell

## Usage Examples
# Run all tests
pwsh scripts/Run-Tests.ps1

# Run specific project tests
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core"

# Run with AI-friendly output
pwsh scripts/Run-Tests.ps1 -AiMode
```

### 1.3 Fix Absolute Paths

**Scan for Local Absolute Paths:**

All local, machine-specific absolute paths must be removed from committed code and documentation. They should be:
1. **Converted to relative paths** when referring to files within the repository
2. **Changed to generic example paths** when used as examples

**Areas to Check:**
- [ ] Source code files (*.cs, *.csproj)
- [ ] Documentation files (*.md)
- [ ] Configuration files (*.json, *.yml, *.yaml, *.xml)
- [ ] Scripts (*.ps1, *.sh)
- [ ] Test files
- [ ] Sample projects
- [ ] Build files (Directory.Build.props, etc.)
- [ ] GitHub Actions workflows
- [ ] CLAUDE.md and ai-docs/

**Examples of Problems:**
```markdown
❌ WRONG - Specific user path:
/Users/philcarbone/src/whizbang/tools/Whizbang.CLI

✅ CORRECT - Relative path:
tools/Whizbang.CLI

❌ WRONG - Specific user path in example:
cd /Users/philcarbone/src/whizbang-lib.github.io
npm install

✅ CORRECT - Generic example path:
cd /path/to/whizbang-lib.github.io  # or <workspace>/whizbang-lib.github.io
npm install

❌ WRONG - Absolute path in config:
"workspaceFolder": "/Users/philcarbone/src/whizbang"

✅ CORRECT - Relative or workspace variable:
"workspaceFolder": "${workspaceFolder}"
```

**Search Commands:**
```bash
# Search for common absolute path patterns
grep -r "/Users/" --include="*.cs" --include="*.md" --include="*.json" --include="*.yml" .
grep -r "C:\\" --include="*.cs" --include="*.md" --include="*.json" --include="*.yml" .
grep -r "/home/" --include="*.cs" --include="*.md" --include="*.json" --include="*.yml" .

# Search in PowerShell
Get-ChildItem -Recurse -Include *.cs,*.md,*.json,*.yml | Select-String -Pattern "/Users/|C:\\|/home/"
```

**Exceptions:**
- Root CLAUDE.md (`/Users/philcarbone/src/CLAUDE.md`) - Not in source control, personal file
- Workspace references in documentation that explicitly say "replace with your path"

**Generic Path Examples to Use:**
- `<workspace>/project-name` - For user workspace paths
- `/path/to/project` - Generic Unix-style path
- `C:\path\to\project` - Generic Windows-style path
- `${workspaceFolder}` - For VSCode/IDE configuration
- `./relative/path` - For relative paths from repository root
- `../sibling/path` - For paths to sibling repositories

---

## 2. Build Quality - Zero Errors/Warnings (Alpha)

### 2.1 Compiler Errors
- [ ] Run `dotnet build` - Must succeed with 0 errors
- [ ] Fix all compilation errors
- [ ] Verify all projects build successfully

### 2.2 Compiler Warnings
- [ ] Run `dotnet build` - Must complete with 0 warnings
- [ ] Fix all compiler warnings
- [ ] Verify `TreatWarningsAsErrors` setting (currently false, consider enabling)
- [ ] Note: `EnforceCodeStyleInBuild` is enabled (good!)

### 2.3 Analyzer Warnings
- [ ] Fix all Roslynator warnings
- [ ] Fix all AOT analyzer warnings (IL2026, IL2046, IL2075)
- [ ] Fix all banned symbol warnings
- [ ] Fix all code style violations (IDE1006, etc.)
- [ ] Review all TODO/HACK/FIXME comments
- [ ] Consider enabling `TreatWarningsAsErrors` after all warnings are fixed

### 2.4 Warning Suppression Audit

**CRITICAL**: Whizbang has strict AOT compatibility requirements. Warning suppressions must be rare, approved, and documented.

#### Find All Suppressions
- [ ] Search for all pragma warning disable directives:
  ```bash
  # Find all pragma disables
  grep -rn "#pragma warning disable" src/ --include="*.cs"

  # Find all SuppressMessage attributes
  grep -rn "SuppressMessage" src/ --include="*.cs"

  # PowerShell alternative
  Get-ChildItem -Recurse -Include *.cs | Select-String -Pattern "#pragma warning disable|SuppressMessage"
  ```

#### Audit Each Suppression

For EACH suppression found:

**1. Verify it's in appropriate code:**
- ✅ **Test projects** - Reflection allowed when no alternative exists
- ✅ **Generator projects** - Reflection required for Roslyn APIs
- ❌ **Production code** (Whizbang.Core, etc.) - Should have ZERO suppressions for AOT warnings

**2. Check if it's approved:**
- [ ] Is there a code comment explaining WHY it's needed?
- [ ] Is the suppression as narrow as possible (single line vs. entire file)?
- [ ] Is it suppressing an AOT warning (IL2026, IL2046, IL2075)?
  - If YES in production code → **BLOCK RELEASE**, must fix
  - If YES in test code → Needs documentation + wishlist

**3. Document approved suppressions:**

Every approved suppression MUST have:
- Inline comment explaining why it's needed
- Reference to documented wishlist item
- Link to tracking issue or documentation

**Example of properly documented suppression:**

```csharp
// Test infrastructure requires reflection for mock generation
// See: docs/wishlists/reflection-free-mocking.md
// Tracking: https://github.com/whizbang-lib/whizbang/issues/XXX
#pragma warning disable IL2026 // RequiresUnreferencedCode
var mock = CreateMockWithReflection(type);
#pragma warning restore IL2026
```

#### Create Wishlist Documentation

- [ ] Create `docs/wishlists/` directory in documentation repo
- [ ] For each approved suppression, create or update wishlist document

**Wishlist document structure:**

```markdown
# Wishlist: [Feature Name]

## Current Limitation
[Explain what currently requires suppression]

## Suppression Locations
- `Whizbang.Core.Tests/MockFactory.cs:45` - IL2026 for mock creation
- [List all locations]

## Desired C# Features

### 1. [Feature Name]
**Likelihood**: [High/Medium/Low]
**Status**: [Proposed/In Preview/Released]
**Last Checked**: 2024-12-22

[Description of feature that would resolve this]

**Research:**
- [Link to C# design proposal]
- [Link to GitHub issue]
- [Summary of community discussion]

**Would resolve**:
- [Specific suppressions this would eliminate]

### 2. [Alternative Feature]
[Same structure]

## Alternative Solutions Explored
- [Solutions tried that didn't work]
- [Why they didn't work]

## Re-evaluation Notes
- **2024-12-22**: Initial wishlist created
- [Future re-evaluation entries]
```

#### Validate Wishlist Against Current State

**Before each release:**
- [ ] For each wishlist item, perform web search to check current status:
  ```
  Search: "C# 13 [feature name] proposal status 2025"
  Search: "dotnet runtime [feature name] design"
  Search: ".NET [next version] [feature name]"
  ```

- [ ] Update wishlist with findings:
  - Is it now available? → Remove suppression and update code!
  - Is it in preview? → Update status, prepare migration plan
  - Still proposed? → Update status
  - Rejected/unlikely? → Mark as such, consider alternative approaches

- [ ] Document validation in wishlist:
  ```markdown
  ## Re-evaluation Notes
  - **2024-12-22**: Initial wishlist created
  - **2025-03-15**: Checked for C# 13 preview - feature still proposed
  - **2025-06-20**: Feature released in .NET 9 preview! Planning migration
  - **2025-09-01**: Suppression removed, using new feature
  ```

#### Suppression Categories

**APPROVED (with documentation):**
- Test infrastructure requiring reflection (when no alternative exists)
- Generator projects using Roslyn APIs (requires reflection by nature)
- Third-party library limitations (document, wishlist for library update)

**NEVER APPROVED:**
- Production code (Whizbang.Core, etc.) suppressing AOT warnings
- Suppressions for convenience ("easier to suppress than fix")
- Broad suppressions (file-level, assembly-level)
- Undocumented suppressions

#### Action Items for This Release

- [ ] Audit all suppressions (run grep commands above)
- [ ] Document or remove undocumented suppressions
- [ ] Create wishlist documents for approved suppressions
- [ ] Perform initial web searches for wishlist items
- [ ] Add tracking issues for all approved suppressions
- [ ] Verify zero suppressions in production code (except documented, approved cases)
- [ ] Add this audit to release checklist for future releases

#### Example: Test Code Reflection Suppression

**Current code (needs documentation):**
```csharp
// ❌ WRONG: Undocumented suppression
#pragma warning disable IL2026
var instance = Activator.CreateInstance(type);
#pragma warning restore IL2026
```

**Properly documented:**
```csharp
// ✅ CORRECT: Documented with wishlist reference
// Test infrastructure requires Activator.CreateInstance for dynamic mock creation
// Wishlist: Static abstract interface members for generic creation
// See: docs/wishlists/static-abstract-members.md
// Tracking: https://github.com/whizbang-lib/whizbang/issues/42
// Last validated: 2024-12-22 - C# 13 doesn't include this feature yet
#pragma warning disable IL2026 // RequiresUnreferencedCode for Activator.CreateInstance
var instance = Activator.CreateInstance(type);
#pragma warning restore IL2026
```

---

## 3. Documentation & Standards Verification (Alpha)

### 3.1 Verify Standards Compliance

**TDD Compliance:**
- [ ] Review `ai-docs/tdd-strict.md`
- [ ] Verify RED → GREEN → REFACTOR cycle followed
- [ ] Ensure tests define behavior
- [ ] Verify no implementation without tests

**Testing Standards:**
- [ ] Review `ai-docs/testing-tunit.md`
- [ ] Verify all test methods end with `Async` suffix
- [ ] Check TUnit CLI usage patterns
- [ ] Verify Rocks mocking patterns
- [ ] Verify Bogus usage for test data

**EF Core Patterns:**
- [ ] Review `ai-docs/efcore-10-usage.md`
- [ ] Verify PostgreSQL JsonB usage
- [ ] Verify UUIDv7 usage
- [ ] Check DbContext implementations
- [ ] Verify owned entities and complex types

**AOT Compliance:**
- [ ] Review `ai-docs/aot-requirements.md`
- [ ] Verify Whizbang.Core has zero reflection
- [ ] Verify source generators use reflection appropriately
- [ ] Check JSON serialization is source-generated
- [ ] Verify AOT warnings are treated as errors

**Code Standards:**
- [ ] Review `ai-docs/code-standards.md`
- [ ] Verify K&R/Egyptian braces everywhere
- [ ] Check async method naming (all end with Async)
- [ ] Verify XML documentation on all public APIs
- [ ] Check namespace organization

**Boy Scout Rule:**
- [ ] Review `ai-docs/boy-scout-rule.md`
- [ ] Verify code is left better than found
- [ ] Check for cleanup of nearby code
- [ ] Verify no technical debt introduced

**Documentation Maintenance:**
- [ ] Review `ai-docs/documentation-maintenance.md`
- [ ] Verify all public APIs have `<docs>` tags
- [ ] Check documentation is synchronized with code
- [ ] Verify version awareness in documentation

**Code Formatting:**
- [ ] Run `dotnet format --verify-no-changes`
- [ ] Must pass with no changes needed
- [ ] Fix any formatting issues

**Source Generator Standards:**
- [ ] Review `src/Whizbang.Generators/ai-docs/template-system.md`
- [ ] Review `src/Whizbang.Generators/ai-docs/performance-principles.md`
- [ ] Verify all generators use templates/snippets where appropriate
- [ ] Verify all generators follow syntactic filtering patterns
- [ ] Check all info records are sealed records (not classes)
- [ ] Verify no cross-dependencies between generators

**StringBuilder Usage Audit:**

StringBuilder should be used **sparingly** in generators. Prefer templates/snippets for maintainability.

**When StringBuilder is acceptable:**
- Small, simple code generation (1-5 lines)
- Dynamic list building (e.g., multiple if statements)
- Joining collection items with separators

**When to use Templates/Snippets instead:**
- Complete method bodies
- Complete class structures
- Any code block > 10 lines
- Code with complex formatting/indentation
- Code that benefits from IDE support during development

**Audit checklist:**
- [ ] Find all StringBuilder usage in Whizbang.Generators: `grep -r "StringBuilder" src/Whizbang.Generators/*.cs`
- [ ] Review each usage:
  - [ ] Is it building a large code block (>10 lines)? → Should use template
  - [ ] Is it building structured code (classes, methods)? → Should use template
  - [ ] Is it simple list building? → Acceptable
  - [ ] Could a snippet replace it? → Consider refactoring to snippet
- [ ] Document findings and create refactoring tasks if needed
- [ ] For acceptable StringBuilders, add code comment explaining why StringBuilder is appropriate

**Examples:**

```csharp
// ✅ ACCEPTABLE: Simple list building
var sb = new StringBuilder();
foreach (var receptor in receptors) {
  sb.AppendLine($"services.AddTransient<{receptor.InterfaceType}, {receptor.ClassName}>();");
}

// ❌ SHOULD USE TEMPLATE: Large structured code
var sb = new StringBuilder();
sb.AppendLine("public class GeneratedDispatcher : Dispatcher {");
sb.AppendLine("  public GeneratedDispatcher(IServiceProvider services) : base(services) { }");
sb.AppendLine("  protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(");
// ... 50 more lines
// → This should be a template with region replacement!

// ✅ ACCEPTABLE: Dynamic conditions (would be awkward in template)
var sb = new StringBuilder();
if (includeMetrics) {
  sb.AppendLine("_metrics.Increment();");
}
if (includeLogging) {
  sb.AppendLine("_logger.LogInformation(\"Processing\");");
}

// ⚠️ CONSIDER SNIPPET: Repeated small patterns
var sb = new StringBuilder();
sb.AppendLine($"if (messageType == typeof({messageType})) {{");
sb.AppendLine($"  return _serviceProvider.GetService<{receptorType}>();");
sb.AppendLine("}");
// → Could extract this as a reusable snippet
```

**Error Code Organization and Documentation:**
- [ ] Collect all diagnostic error codes from `DiagnosticDescriptors.cs`
- [ ] Organize error codes into categories by 100s:
  - 100-199: Receptor/Dispatcher errors
  - 200-299: Message/Event errors
  - 300-399: Perspective/Lens errors
  - 400-499: Configuration errors
  - 500-599: Generation errors
  - (Adjust categories based on actual error types)
- [ ] Rename error codes if needed to fit categorical scheme
- [ ] Add `<docs>` tags to all DiagnosticDescriptor declarations
- [ ] Document each error code in documentation site:
  - Error code number and title
  - What it means
  - Common causes
  - How to fix it
  - Code examples showing the error and fix
- [ ] Create documentation page: `docs/diagnostics/error-codes.md`
- [ ] Organize by category with table of contents
- [ ] Regenerate code-docs mapping after adding `<docs>` tags
- [ ] Validate all diagnostic documentation links

### 3.2 Create Standard Repository Files

#### CHANGELOG.md
- [ ] Create `CHANGELOG.md` in repository root

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-alpha] - 2025-XX-XX

### Added
- Initial alpha release
- Core messaging infrastructure (Dispatcher, Receptors, Message Envelopes)
- Event-driven architecture support
- CQRS patterns and implementations
- Event sourcing foundations
- Zero-reflection, AOT-compatible design
- PostgreSQL support with UUIDv7 and JsonB
- EF Core 10 integration with compiled models
- Dapper support for PostgreSQL and SQLite
- Azure Service Bus transport integration
- Whizbang CLI tool for code generation and management
- Comprehensive test suite with TUnit and Rocks (100% coverage)
- Source generators for zero-reflection functionality
- Observability and logging abstractions
- Partitioning and sequencing support
- Work coordination and batch processing

### Documentation
- Comprehensive API documentation at https://whizbang-lib.github.io
- Getting started guides and tutorials
- Code examples with verified tests
- Architecture documentation
- AI-enhanced documentation with MCP server integration

### Performance
- Baseline benchmarks established
- Optimized for .NET 10 and Native AOT

### Infrastructure
- GitHub Actions CI/CD pipelines
- SonarCloud integration for code quality
- Codecov integration for test coverage
- Dependabot for dependency management
- GitVersion for semantic versioning
```

#### CODE_OF_CONDUCT.md
- [ ] Create `CODE_OF_CONDUCT.md`

```markdown
# Contributor Covenant Code of Conduct

## Our Pledge

We as members, contributors, and leaders pledge to make participation in our
community a harassment-free experience for everyone, regardless of age, body
size, visible or invisible disability, ethnicity, sex characteristics, gender
identity and expression, level of experience, education, socio-economic status,
nationality, personal appearance, race, caste, color, religion, or sexual
identity and orientation.

We pledge to act and interact in ways that contribute to an open, welcoming,
diverse, inclusive, and healthy community.

[Use standard Contributor Covenant 2.1]
Full text: https://www.contributor-covenant.org/version/2/1/code_of_conduct/

## Enforcement

Instances of abusive, harassing, or otherwise unacceptable behavior may be
reported to the community leaders responsible for enforcement at
[INSERT CONTACT EMAIL].

All complaints will be reviewed and investigated promptly and fairly.
```

#### SECURITY.md
- [ ] Create `SECURITY.md`

```markdown
# Security Policy

## Supported Versions

We release patches for security vulnerabilities for the following versions:

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please use GitHub Security Advisories:

1. Go to https://github.com/whizbang-lib/whizbang/security/advisories
2. Click "Report a vulnerability"
3. Provide detailed information about the vulnerability

**Expected response time:** Within 48 hours

We will send you a response indicating the next steps in handling your report.
After the initial reply, we will keep you informed of the progress towards a
fix and full announcement.

## Disclosure Policy

When we receive a security bug report, we will:

1. Confirm the problem and determine affected versions
2. Audit code to find similar problems
3. Prepare fixes for all supported versions
4. Release new security versions as soon as possible

## Comments on this Policy

If you have suggestions on how this process could be improved, please submit
a pull request.
```

#### CONTRIBUTORS.md
- [ ] Create `CONTRIBUTORS.md`

```markdown
# Contributors

Thank you to everyone who has contributed to Whizbang!

## Core Team
- Phil Carbone (@philcarbone) <phil@extravaganza.software>

## Contributors
<!-- This section will be automatically generated from git history -->
<!-- To add yourself: git log --format='%aN <%aE>' | sort -u -->

## Recognition

Contributors are recognized through:
- This file
- Release notes for significant contributions
- Credit in documentation they write or significantly improve
- Mentions in blog posts for major features

## How to Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed contribution guidelines.
```

### 3.3 Update README.md (Beta Phase)

#### Add Badges (top of file)
```markdown
[![Build Status](https://github.com/whizbang-lib/whizbang/workflows/build/badge.svg)](https://github.com/whizbang-lib/whizbang/actions)
[![Test Coverage](https://codecov.io/gh/whizbang-lib/whizbang/branch/main/graph/badge.svg)](https://codecov.io/gh/whizbang-lib/whizbang)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=whizbang-lib_whizbang&metric=alert_status)](https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang)
[![NuGet - Whizbang.Core](https://img.shields.io/nuget/v/Whizbang.Core.svg?label=Whizbang.Core)](https://www.nuget.org/packages/Whizbang.Core/)
[![NuGet - Whizbang.Generators](https://img.shields.io/nuget/v/Whizbang.Generators.svg?label=Whizbang.Generators)](https://www.nuget.org/packages/Whizbang.Generators/)
[![License: MIT](https://img.shields.io/github/license/whizbang-lib/whizbang.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download)
```

#### Update Content
- [ ] Remove "All tests are currently failing by design" status
- [ ] Update version from "Foundation Release" to "v0.1.0"
- [ ] Add installation instructions for each package
- [ ] Add quick start example
- [ ] Add feature highlights
- [ ] Link to comprehensive documentation
- [ ] Add "Getting Help" section
- [ ] Add "Contributing" section reference

### 3.4 CLI Documentation (Beta Phase)

- [ ] Create `tools/Whizbang.CLI/README.md`

```markdown
# Whizbang CLI

Command-line interface for Whizbang code generation and project management.

## Installation

### As Global Tool
dotnet tool install -g Whizbang.CLI

### As Local Tool
dotnet tool install --local Whizbang.CLI

## Commands

### [Add command documentation here]

## Configuration

### [Add configuration options here]

## Usage Examples

### [Add usage examples here]

## Troubleshooting

### [Add common issues and solutions here]

## More Information

Full documentation: https://whizbang-lib.github.io/tools/cli
```

### 3.5 Contributing Guide (Alpha Phase)

- [ ] Create `CONTRIBUTING.md` in whizbang repository

```markdown
# Contributing to Whizbang

Thank you for your interest in contributing to Whizbang!

## Prerequisites

Before you begin, ensure you have:
- **PowerShell 7+** (cross-platform) - https://aka.ms/powershell
- **.NET 10 SDK** - https://dotnet.microsoft.com/download
- **Git** - https://git-scm.com/downloads

## Quick Start

1. **Fork and clone the repository**
   ```bash
   git clone https://github.com/YOUR-USERNAME/whizbang.git
   cd whizbang
   ```

2. **Create a feature branch from develop**
   ```bash
   git checkout develop
   git checkout -b feature/your-feature-name
   ```

3. **Follow the documentation-first approach**
   - Write or update documentation first
   - Create tests based on documentation
   - Implement to make tests pass

4. **Run tests**
   ```powershell
   pwsh scripts/Run-Tests.ps1
   ```

5. **Format code before committing**
   ```bash
   dotnet format
   ```

6. **Submit a pull request**
   - Target the `develop` branch
   - Reference any related issues
   - Ensure all CI checks pass

## Development Workflow

Whizbang follows a **documentation-first, test-driven** development approach:

1. **Document First** - Write or update documentation
2. **Test Second** - Create tests based on documentation (RED)
3. **Implement Third** - Make tests pass (GREEN)
4. **Refactor** - Improve code quality (REFACTOR)

See comprehensive guide: https://whizbang-lib.github.io/contributing

## Standards and Best Practices

### Code Standards
- **All async methods** must end with `Async` suffix
- **K&R/Egyptian braces** for all code (opening brace on same line)
- **100% test coverage** required
- **Zero warnings** policy
- **XML documentation** on all public APIs
- **`<docs>` tags** on public APIs linking to documentation

### Testing
- Use **TUnit** for test framework
- Use **Rocks** for mocking (AOT-compatible)
- Use **Bogus** for test data generation
- Follow **AAA pattern** (Arrange, Act, Assert)
- Test method names: `MethodName_Scenario_ExpectedResultAsync`

### Documentation
- Add `<docs>` tags to public APIs
- Regenerate code-docs mapping after changes
- Update documentation site when changing public APIs

## Repository Structure

```
whizbang/
├── src/                    # Source code
│   ├── Whizbang.Core/     # Core library
│   ├── Whizbang.Generators/ # Source generators
│   └── [other packages]/
├── tests/                  # Test projects
├── samples/                # Sample applications
├── scripts/                # PowerShell scripts
├── ai-docs/               # AI-focused documentation
└── plans/                 # Planning documents
```

## AI Documentation

This repository includes AI-focused documentation in `ai-docs/`:

- **tdd-strict.md** - TDD guidelines (RED/GREEN/REFACTOR)
- **testing-tunit.md** - TUnit, Rocks, and testing patterns
- **efcore-10-usage.md** - EF Core 10 patterns
- **aot-requirements.md** - AOT compatibility requirements
- **code-standards.md** - Code formatting and naming
- **boy-scout-rule.md** - Leave code better than you found it
- **documentation-maintenance.md** - Documentation workflow

See `ai-docs/README.md` for the complete list.

## Running Tests

```powershell
# Run all tests
pwsh scripts/Run-Tests.ps1

# Run specific project
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core"

# Run specific test
pwsh scripts/Run-Tests.ps1 -TestFilter "DispatcherTests"

# AI-friendly output
pwsh scripts/Run-Tests.ps1 -AiMode
```

## Building

```bash
# Clean and build
dotnet clean && dotnet build

# Build specific project
dotnet build src/Whizbang.Core/Whizbang.Core.csproj
```

## Code Quality

Before submitting a PR:

```bash
# Format code (REQUIRED)
dotnet format

# Verify formatting
dotnet format --verify-no-changes

# Build with zero warnings
dotnet build

# Run tests with 100% coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Pull Request Guidelines

### Branch Naming
- Feature: `feature/your-feature-name`
- Bugfix: `bugfix/issue-number-description`
- Documentation: `docs/what-you-changed`

### PR Title Format
- `feat: Add event sourcing support`
- `fix: Correct async naming in Dispatcher`
- `docs: Improve getting started guide`
- `test: Add tests for Receptor validation`

### PR Description
Include:
1. **What Changed** - Clear description
2. **Why** - Motivation for the change
3. **Testing** - How you tested it
4. **Documentation** - Links to updated docs
5. **Breaking Changes** - If applicable

### Review Process
- All PRs require at least 1 review
- All CI checks must pass
- Code coverage must remain at 100%
- No warnings allowed

## Getting Help

- **Documentation questions**: Open an issue with `[docs]` prefix
- **Feature ideas**: Open a discussion
- **Bugs**: Open an issue with bug template
- **General questions**: GitHub Discussions

## Code of Conduct

This project adheres to the Contributor Covenant Code of Conduct.
By participating, you are expected to uphold this code.
See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for details.

## License

By contributing to Whizbang, you agree that your contributions will be
licensed under the MIT License.

---

Thank you for contributing to Whizbang! 🎉
```

---

## 4. CLAUDE.md Organization Strategy (Alpha)

### 4.1 Organization Principle

**Root CLAUDE.md** (`/Users/philcarbone/src/CLAUDE.md`):
- **NOT in source control** (personal, machine-specific)
- Personal preferences and workflow
- Machine-specific paths
- Cross-repository navigation
- Personal AI assistant configuration
- Links to repo-specific CLAUDE.md files

**Repo CLAUDE.md** (`whizbang/CLAUDE.md`, `whizbang-lib.github.io/CLAUDE.md`):
- **IN source control** (team-shared)
- Project-specific standards
- Repository structure
- Development commands
- AI documentation references
- Slash commands for the project
- Technology stack
- Shared team knowledge

### 4.2 Contributor Onboarding

- [ ] Create `.github/SETUP.md`

```markdown
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
```

### 4.3 Decision
- [ ] Document that root CLAUDE.md is personal/optional
- [ ] Ensure repo CLAUDE.md is committed and maintained
- [ ] Update CONTRIBUTING.md to reference setup guide

---

## 5. Build & CI/CD Infrastructure (Alpha)

### 5.1 Create GitHub Actions Workflows

Create `.github/workflows/` directory and add workflow files:

#### build.yml - Build and Format Check
- [ ] Create `.github/workflows/build.yml`

```yaml
name: Build

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --no-restore --configuration Release

    - name: Verify code formatting
      run: dotnet format --verify-no-changes --no-restore
```

#### test.yml - Tests with 100% Coverage
- [ ] Create `.github/workflows/test.yml`

```yaml
name: Test

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]

jobs:
  test:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --no-restore --configuration Release

    - name: Run tests with coverage
      run: dotnet test --no-build --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage

    - name: Upload coverage to Codecov
      uses: codecov/codecov-action@v4
      with:
        token: ${{ secrets.CODECOV_TOKEN }}
        directory: ./coverage
        fail_ci_if_error: true
        flags: unittests
        name: codecov-umbrella

    - name: Verify 100% coverage
      run: |
        # Add script to verify coverage is 100%
        # This will be added after Codecov setup
```

#### quality.yml - SonarCloud Scanning
- [ ] Create `.github/workflows/quality.yml`

```yaml
name: Quality

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]

jobs:
  sonarcloud:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0  # Shallow clones disabled for SonarCloud

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Cache SonarCloud packages
      uses: actions/cache@v4
      with:
        path: ~/.sonar/cache
        key: ${{ runner.os }}-sonar
        restore-keys: ${{ runner.os }}-sonar

    - name: Install SonarCloud scanner
      run: dotnet tool install --global dotnet-sonarscanner

    - name: Build and analyze
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
      run: |
        dotnet-sonarscanner begin /k:"whizbang-lib_whizbang" /o:"whizbang-lib" /d:sonar.token="${SONAR_TOKEN}" /d:sonar.host.url="https://sonarcloud.io"
        dotnet build --configuration Release
        dotnet-sonarscanner end /d:sonar.token="${SONAR_TOKEN}"
```

#### nuget-pack.yml - Validate Package Build
- [ ] Create `.github/workflows/nuget-pack.yml`

```yaml
name: NuGet Pack

on:
  pull_request:
    branches: [ main, develop ]

jobs:
  pack:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --configuration Release --no-restore

    - name: Pack NuGet packages
      run: dotnet pack --configuration Release --no-build --output ./packages

    - name: Validate package metadata
      run: |
        # Add validation script for package metadata
        ls -la ./packages

    - name: Upload packages as artifacts
      uses: actions/upload-artifact@v4
      with:
        name: nuget-packages
        path: ./packages/*.nupkg
```

#### nuget-publish.yml - Publish on Release Tags
- [ ] Create `.github/workflows/nuget-publish.yml`

```yaml
name: NuGet Publish

on:
  push:
    tags:
      - 'v*'  # Trigger on version tags: v0.1.0-alpha, v0.1.0-beta, v0.1.0

jobs:
  publish:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0  # Full history for GitVersion

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Install GitVersion
      run: dotnet tool install --global GitVersion.Tool

    - name: Determine version
      id: gitversion
      run: |
        dotnet-gitversion /output json | jq -r 'to_entries|map("\(.key)=\(.value|tostring)")|.[]' >> $GITHUB_OUTPUT

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --configuration Release --no-restore

    - name: Pack
      run: dotnet pack --configuration Release --no-build --output ./packages /p:PackageVersion=${{ steps.gitversion.outputs.SemVer }}

    - name: Publish to NuGet
      run: dotnet nuget push ./packages/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate

    - name: Create GitHub Release
      uses: softprops/action-gh-release@v1
      with:
        files: ./packages/*.nupkg
        body: |
          Release ${{ steps.gitversion.outputs.SemVer }}

          See [CHANGELOG.md](CHANGELOG.md) for details.
        draft: false
        prerelease: ${{ contains(steps.gitversion.outputs.SemVer, '-') }}
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

#### dependabot.yml - Dependency Updates
- [ ] Create `.github/dependabot.yml`

```yaml
version: 2
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
    groups:
      dotnet:
        patterns:
          - "Microsoft.*"
          - "System.*"
      testing:
        patterns:
          - "TUnit*"
          - "Rocks"
      efcore:
        patterns:
          - "Microsoft.EntityFrameworkCore*"
          - "Npgsql.EntityFrameworkCore.PostgreSQL"
      analyzers:
        patterns:
          - "*.Analyzers"
          - "Roslynator.*"
```

### 5.2 GitFlow Branching Strategy (Alpha)

#### Create Branches
- [ ] Create `develop` branch:
  ```bash
  git checkout -b develop
  git push -u origin develop
  ```

#### Configure Branch Protection Rules
- [ ] **main branch protection:**
  - Require pull request before merging
  - Require at least 1 approval
  - Require status checks to pass:
    - build
    - test
    - quality
  - Do not allow direct pushes
  - Require linear history (optional)

- [ ] **develop branch protection:**
  - Require pull request before merging
  - Require status checks to pass:
    - build
    - test
  - Allow direct pushes from maintainers (optional)

#### Document Branching Strategy
- [ ] Add to CONTRIBUTING.md:

```markdown
## Branching Strategy

We use GitFlow:

### Branch Types
- **main** - Production releases only (v0.1.0, v0.2.0, etc.)
- **develop** - Integration branch for features
- **feature/** - New features (branch from develop)
- **bugfix/** - Bug fixes (branch from develop)
- **release/** - Release preparation (branch from develop)
- **hotfix/** - Critical fixes (branch from main)

### Merge Strategy

| Merge Type | Strategy | Reason |
|------------|----------|--------|
| **feature → develop** | **Squash merge** | Clean atomic commits, hides iterative development |
| **bugfix → develop** | **Squash merge** | Clean atomic commits |
| **develop → main (release)** | **Squash merge** | Single clean release commit |
| **hotfix → main/develop** | **Merge commit** | Preserves audit trail for critical fixes |

**Squash merge** is the default for all PRs except hotfixes. This keeps the main branch history clean and meaningful.

### Workflow

#### Feature Development
1. Branch from `develop`: `git checkout -b feature/my-feature develop`
2. Develop and test
3. Create PR to `develop`
4. After review and CI pass, **squash merge** to `develop`

#### Bug Fixes
1. Branch from `develop`: `git checkout -b bugfix/issue-123 develop`
2. Fix and test
3. Create PR to `develop`
4. After review and CI pass, **squash merge** to `develop`

#### Release Preparation
1. Branch from `develop`: `git checkout -b release/v0.1.0 develop`
2. Final testing, documentation updates, version bumps
3. Create PR to `main`
4. After all CI passes, **squash merge** to `main` with message: `release: v0.1.0`
5. Tag main: `git tag v0.1.0`
6. Push tag: `git push origin v0.1.0` (triggers NuGet publish)
7. Merge main back to develop (if needed)

#### Hotfixes
1. Branch from `main`: `git checkout -b hotfix/critical-issue main`
2. Fix and test
3. Create PR to `main` AND `develop`
4. After approval, **merge commit** (not squash) to both branches
   - Use merge commit to preserve the full audit trail for critical fixes
5. Tag main: `git tag -a v0.1.1 -m "Hotfix v0.1.1"`
6. Push tag: `git push origin v0.1.1`
```

### 5.3 GitVersion Setup (Alpha)

#### Install GitVersion
- [ ] Install as local tool:
  ```bash
  dotnet tool install --local GitVersion.Tool
  dotnet tool restore
  ```

- [ ] Update `.config/dotnet-tools.json` (should be created automatically)

#### Create GitVersion Configuration
- [ ] Create `GitVersion.yml` in repository root:

```yaml
mode: ContinuousDeployment
branches:
  main:
    tag: ''
    increment: Patch
    prevent-increment-of-merged-branch-version: true
    track-merge-target: false
    regex: ^main$
    source-branches: ['release', 'hotfix']
  develop:
    tag: alpha
    increment: Minor
    prevent-increment-of-merged-branch-version: false
    track-merge-target: true
    regex: ^develop$
    source-branches: ['feature', 'bugfix']
  release:
    tag: beta
    increment: Patch
    prevent-increment-of-merged-branch-version: true
    track-merge-target: false
    regex: ^release[/-]
    source-branches: ['develop']
  feature:
    tag: alpha.{BranchName}
    increment: Minor
    regex: ^feature[/-]
    source-branches: ['develop']
  bugfix:
    tag: alpha.{BranchName}
    increment: Patch
    regex: ^bugfix[/-]
    source-branches: ['develop']
  hotfix:
    tag: ''
    increment: Patch
    regex: ^hotfix[/-]
    source-branches: ['main']

ignore:
  sha: []

merge-message-formats: {}
```

#### Integrate with Build
- [ ] Update `Directory.Build.props` to use GitVersion:

```xml
<!-- GitVersion Integration -->
<PropertyGroup Condition="'$(GitVersion_SemVer)' != ''">
  <Version>$(GitVersion_SemVer)</Version>
  <FileVersion>$(GitVersion_AssemblySemVer)</FileVersion>
  <InformationalVersion>$(GitVersion_InformationalVersion)</InformationalVersion>
  <PackageVersion>$(GitVersion_SemVer)</PackageVersion>
</PropertyGroup>

<!-- Fallback versions when GitVersion is not available -->
<PropertyGroup Condition="'$(GitVersion_SemVer)' == ''">
  <Version>0.1.0-alpha</Version>
  <FileVersion>0.1.0.0</FileVersion>
  <InformationalVersion>0.1.0-alpha</InformationalVersion>
  <PackageVersion>0.1.0-alpha</PackageVersion>
</PropertyGroup>
```

#### Document Version Numbering
- [ ] Add to repository documentation:

```markdown
## Version Numbering

Whizbang uses [Semantic Versioning](https://semver.org/) with GitVersion for automatic version calculation.

### Version Format
- **Stable releases**: `MAJOR.MINOR.PATCH` (e.g., 0.1.0, 1.0.0)
- **Beta releases**: `MAJOR.MINOR.PATCH-beta.N` (e.g., 0.1.0-beta.1)
- **Alpha releases**: `MAJOR.MINOR.PATCH-alpha.N` (e.g., 0.1.0-alpha.1)

### Branch-Based Versions
- **main**: Stable versions (0.1.0)
- **release/**: Beta versions (0.1.0-beta.1)
- **develop**: Alpha versions (0.1.0-alpha.23)
- **feature/**: Feature-specific alpha (0.1.0-alpha.feature-name.5)

### Creating Releases
Versions are automatically calculated by GitVersion based on:
- Branch name
- Commit messages
- Git tags

To release:
1. Merge to release branch: creates beta version
2. Merge to main: creates stable version
3. Tag main branch: `git tag -a v0.1.0 -m "Release v0.1.0"`
4. Push tag: `git push origin v0.1.0`
5. GitHub Actions automatically publishes to NuGet
```

---

## 6. NuGet Package Configuration (Alpha)

### 6.1 Add Package Metadata to Directory.Build.props

- [ ] Update `Directory.Build.props` with package metadata:

```xml
<!-- Package Metadata -->
<PropertyGroup>
  <Authors>Phil Carbone</Authors>
  <Company>whizbang-lib</Company>
  <Product>Whizbang</Product>
  <Copyright>Copyright © 2025 whizbang-lib</Copyright>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageProjectUrl>https://whizbang-lib.github.io</PackageProjectUrl>
  <RepositoryUrl>https://github.com/whizbang-lib/whizbang</RepositoryUrl>
  <RepositoryType>git</RepositoryType>
  <PackageTags>event-driven;cqrs;event-sourcing;aot;dotnet;zero-reflection;messaging;dispatcher;postgresql;efcore;dapper;azure-servicebus</PackageTags>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PackageIcon>icon.png</PackageIcon>

  <!-- Source Link -->
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>

  <!-- Symbols -->
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>

  <!-- Deterministic builds -->
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>

<!-- Include README and icon in packages -->
<ItemGroup>
  <None Include="$(MSBuildThisFileDirectory)README.md" Pack="true" PackagePath="\" />
  <None Include="$(MSBuildThisFileDirectory)icon.png" Pack="true" PackagePath="\" />
</ItemGroup>
```

### 6.2 Create Package Assets

#### Package Icon
- [ ] Copy icon from docs repo to library repo root
- [ ] Ensure icon is 128x128 PNG
- [ ] Name it `icon.png`
- [ ] Verify it's referenced in Directory.Build.props

#### Configure Packable Projects
- [ ] Set `<IsPackable>true</IsPackable>` in library projects
- [ ] Set `<IsPackable>false</IsPackable>` in test projects
- [ ] Set `<IsPackable>false</IsPackable>` in sample projects

### 6.3 Packages to Publish

#### Core Packages
- [ ] **Whizbang.Core** - Core messaging infrastructure
  - Set `<PackageDescription>` in .csproj
  - Verify `<IsPackable>true</IsPackable>`

- [ ] **Whizbang.Generators** - Source generators
  - Set `<PackageType>Analyzer</PackageType>`
  - Set `<PackageDescription>`
  - Verify `<IsPackable>true</IsPackable>`

- [ ] **Whizbang.Testing** - Testing utilities
  - Set `<PackageDescription>`
  - Verify `<IsPackable>true</IsPackable>`

#### Data Packages
- [ ] **Whizbang.Data.Schema** - Schema management
- [ ] **Whizbang.Data.Postgres** - PostgreSQL support
- [ ] **Whizbang.Data.Dapper.Postgres** - Dapper + PostgreSQL
- [ ] **Whizbang.Data.Dapper.Sqlite** - Dapper + SQLite
- [ ] **Whizbang.Data.Dapper.Custom** - Custom Dapper implementations
- [ ] **Whizbang.Data.EFCore.Postgres** - EF Core + PostgreSQL
- [ ] **Whizbang.Data.EFCore.Postgres.Generators** - EF Core source generators
- [ ] **Whizbang.Data.EFCore.Custom** - Custom EF Core implementations

#### Hosting & Transport Packages
- [ ] **Whizbang.Hosting.Azure.ServiceBus** - Azure Service Bus hosting
- [ ] **Whizbang.Transports.AzureServiceBus** - Azure Service Bus transport

#### Tools
- [ ] **Whizbang.CLI** - Command-line tool
  - Set `<PackAsTool>true</PackAsTool>`
  - Set `<ToolCommandName>whizbang</ToolCommandName>`
  - Set `<PackageDescription>`

### 6.4 Verify Package Configuration

- [ ] Build packages locally:
  ```bash
  dotnet pack --configuration Release --output ./packages
  ```

- [ ] Inspect package contents:
  ```bash
  # On Windows
  7z l packages/Whizbang.Core.0.1.0-alpha.nupkg

  # On macOS/Linux
  unzip -l packages/Whizbang.Core.0.1.0-alpha.nupkg
  ```

- [ ] Verify package metadata:
  ```bash
  nuget spec packages/Whizbang.Core.0.1.0-alpha.nupkg
  ```

---

## 7. Quality Assurance

### 7.1 Testing (Alpha)

#### Run Full Test Suite
- [ ] Run all tests with parallel execution:
  ```bash
  dotnet test --max-parallel-test-modules 8
  ```

#### Generate Coverage Report
- [ ] Run tests with coverage:
  ```bash
  dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage
  ```

- [ ] Generate coverage report:
  ```bash
  # Install reportgenerator if not already installed
  dotnet tool install -g dotnet-reportgenerator-globaltool

  # Generate HTML report
  reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:"coverage/report" -reporttypes:Html
  ```

#### Verify 100% Coverage
- [ ] **MANDATORY: Verify coverage = 100%**
- [ ] Review coverage report in `coverage/report/index.html`
- [ ] Identify and fix any missing coverage
- [ ] Re-run tests until 100% achieved

#### Fix Failing Tests
- [ ] Review all test failures
- [ ] Fix implementation or adjust tests as needed
- [ ] Ensure all tests pass

#### Review Test Organization
- [ ] Verify test naming follows convention: `MethodName_Scenario_ExpectedResultAsync`
- [ ] Check that all async test methods end with `Async` suffix
- [ ] Verify AAA pattern (Arrange, Act, Assert) is followed
- [ ] Ensure tests are organized by feature/component

#### Verify Examples Have Tests
- [ ] Check that all documentation examples have corresponding tests
- [ ] Verify tests are in `tests/Whizbang.Documentation.Tests/`
- [ ] Ensure test names reference documentation sections

### 7.2 Code Quality (Alpha)

#### Build Verification
- [ ] Run clean build:
  ```bash
  dotnet clean && dotnet build --configuration Release
  ```

- [ ] **Verify 0 errors**
- [ ] **Verify 0 warnings**

#### Code Formatting
- [ ] Run format verification:
  ```bash
  dotnet format --verify-no-changes
  ```

- [ ] If changes needed, run:
  ```bash
  dotnet format
  ```

- [ ] Verify formatting passes

#### Analyzer Warnings
- [ ] Review and fix all Roslynator warnings
- [ ] Review and fix all AOT analyzer warnings (IL2026, IL2046, IL2075)
- [ ] Review and fix all banned symbol warnings
- [ ] Review and fix all code style violations (IDE1006, etc.)

#### Code Review
- [ ] Review all TODO comments - resolve or create issues
- [ ] Review all HACK comments - refactor or document why needed
- [ ] Review all FIXME comments - fix or create issues
- [ ] Remove or update commented-out code

#### AOT Compatibility
- [ ] Run AOT analysis on Whizbang.Core:
  ```bash
  dotnet publish -c Release -r linux-x64 --self-contained
  ```

- [ ] Verify no AOT warnings for production code
- [ ] Check that reflection is only in Whizbang.Generators

### 7.3 Performance Testing (Beta Phase)

#### Run Benchmarks
- [ ] Navigate to benchmarks directory
- [ ] Run benchmarks:
  ```bash
  dotnet run -c Release --project benchmarks/Whizbang.Benchmarks
  ```

#### Document Baseline Performance
- [ ] Create or update `benchmarks/README.md`
- [ ] Record baseline metrics:
  - Message dispatch throughput
  - Serialization/deserialization performance
  - Database operation performance
  - Memory allocations

#### Identify Performance Issues
- [ ] Review benchmark results
- [ ] Compare with previous baselines (if available)
- [ ] Identify any regressions
- [ ] Create issues for significant performance problems

---

## 8. Documentation Publishing

### 8.1 Documentation Site Build & Deploy (Beta Phase)

In `whizbang-lib.github.io` repository:

#### Review Documentation
- [ ] Review all documentation for accuracy
- [ ] Verify all code examples are correct
- [ ] Check for broken links
- [ ] Verify all images have alt text
- [ ] Review SEO metadata

#### Version Management
- [ ] Move relevant docs from `drafts/` to `v0.1.0/` folder
- [ ] Update version metadata in `_folder.md` files
- [ ] Verify version dropdown shows v0.1.0
- [ ] Check that draft features are clearly marked

#### Regenerate Mappings
- [ ] Regenerate code-docs mapping:
  ```bash
  cd /Users/philcarbone/src/whizbang-lib.github.io
  node src/scripts/generate-code-docs-map.mjs
  ```

- [ ] Regenerate code-tests mapping:
  ```bash
  node src/scripts/generate-code-tests-map.mjs
  ```

- [ ] Commit updated mapping files

#### Validate Links
- [ ] Run documentation validation:
  ```bash
  # Use slash command
  /verify-links

  # Or use MCP tool
  mcp__whizbang-docs__validate-doc-links()
  ```

- [ ] Fix any broken links
- [ ] Re-validate until all links pass

#### Build and Test
- [ ] Build production site:
  ```bash
  npm run build
  ```

- [ ] Preview production build:
  ```bash
  npm run preview
  ```

- [ ] Test site locally:
  - Verify navigation works
  - Test search functionality
  - Check version dropdown
  - Verify code examples render correctly
  - Test responsive design

#### Deploy to GitHub Pages
- [ ] Commit all changes
- [ ] Push to main branch
- [ ] Verify GitHub Actions deployment succeeds
- [ ] Test live site at https://whizbang-lib.github.io
- [ ] Verify all functionality on live site

### 8.2 Add Blog Section (GA Phase)

#### Create Blog Structure
- [ ] Create `src/assets/blog/` directory
- [ ] Create blog post template
- [ ] Configure Angular routing for blog
- [ ] Create blog list component
- [ ] Create blog post component

#### Create First Blog Post
- [ ] Create `src/assets/blog/2025-XX-XX-v0.1.0-release.md`

**Topics to cover:**
- Release announcement
- Key features overview
- Getting started guide
- Performance highlights
- Future roadmap
- Thank contributors

#### Configure Blog Features
- [ ] Add blog navigation to main menu
- [ ] Create blog post listing with pagination
- [ ] Add blog post metadata (date, author, tags)
- [ ] Implement blog post search
- [ ] Add RSS feed for blog

#### SEO Optimization
- [ ] Add structured data for blog posts
- [ ] Generate sitemap including blog posts
- [ ] Add Open Graph tags for social sharing
- [ ] Configure Twitter Card metadata

### 8.3 Library Documentation (Alpha Phase)

#### XML Documentation
- [ ] Verify all public APIs have XML documentation
- [ ] Check summary tags are descriptive
- [ ] Verify param tags for all parameters
- [ ] Check returns tags for all return values
- [ ] Verify exception tags for thrown exceptions

#### Documentation Tags
- [ ] Update all `<docs>` tags to reference correct documentation
- [ ] Add `<tests>` tags where needed for complex scenarios
- [ ] Verify tags point to valid documentation paths

#### CLAUDE.md Updates
- [ ] Review `CLAUDE.md` for accuracy
- [ ] Update if any standards have changed
- [ ] Verify all ai-docs references are correct
- [ ] Update slash command references

#### AI Documentation Updates
- [ ] Review all `ai-docs/` files for accuracy
- [ ] Update if any patterns have changed
- [ ] Add new documentation if needed
- [ ] Verify examples in ai-docs are current

---

## 9. Open Source Services Setup

### 9.1 SonarCloud Setup (Alpha)

#### Create SonarCloud Organization
- [ ] Go to https://sonarcloud.io
- [ ] Sign in with GitHub
- [ ] Create organization: `whizbang-lib`
- [ ] Link to GitHub repository

#### Configure Project
- [ ] Import `whizbang` repository
- [ ] Set project key: `whizbang-lib_whizbang`
- [ ] Configure project name and description

#### Set Quality Gates
- [ ] Navigate to Quality Gates
- [ ] Create custom quality gate or use default
- [ ] Set requirements:
  - Coverage: 100%
  - Duplications: < 3%
  - Maintainability Rating: A
  - Reliability Rating: A
  - Security Rating: A
  - 0 bugs
  - 0 vulnerabilities
  - 0 code smells (or very low tolerance)

#### Create Configuration File
- [ ] Create `sonar-project.properties` in repository root:

```properties
sonar.projectKey=whizbang-lib_whizbang
sonar.organization=whizbang-lib
sonar.host.url=https://sonarcloud.io

# Source code
sonar.sources=src
sonar.tests=tests

# Exclusions
sonar.exclusions=**/*.Generated.cs,**/.whizbang-generated/**

# Coverage
sonar.cs.opencover.reportsPaths=**/coverage.opencover.xml
sonar.coverage.exclusions=**/*.Tests/**,**/samples/**,**/benchmarks/**

# Language
sonar.language=cs
sonar.sourceEncoding=UTF-8
```

#### Test Integration Locally
- [ ] Install SonarScanner:
  ```bash
  dotnet tool install --global dotnet-sonarscanner
  ```

- [ ] Run analysis locally:
  ```bash
  dotnet sonarscanner begin /k:"whizbang-lib_whizbang" /o:"whizbang-lib" /d:sonar.token="YOUR_TOKEN" /d:sonar.host.url="https://sonarcloud.io"
  dotnet build --configuration Release
  dotnet sonarscanner end /d:sonar.token="YOUR_TOKEN"
  ```

#### Add GitHub Action
- [ ] Verify `.github/workflows/quality.yml` exists (created in section 5.1)
- [ ] Add `SONAR_TOKEN` to GitHub repository secrets
- [ ] Test GitHub Action by pushing a commit

#### VSCode Integration
- [ ] Install SonarLint extension
- [ ] Create `.vscode/extensions.json`:

```json
{
  "recommendations": [
    "SonarSource.sonarlint-vscode",
    "ms-dotnettools.csharp",
    "ms-dotnettools.csdevkit",
    "ms-vscode.powershell"
  ]
}
```

- [ ] Create `.vscode/settings.json` (if needed):

```json
{
  "sonarlint.connectedMode.project": {
    "projectKey": "whizbang-lib_whizbang"
  }
}
```

- [ ] Commit `.vscode/` configuration

### 9.2 Codecov Setup (Alpha)

#### Create Codecov Account
- [ ] Go to https://codecov.io
- [ ] Sign in with GitHub
- [ ] Add repository: `whizbang-lib/whizbang`

#### Configure Coverage Thresholds
- [ ] Create `codecov.yml` in repository root:

```yaml
coverage:
  status:
    project:
      default:
        target: 100%
        threshold: 0%
        if_ci_failed: error
    patch:
      default:
        target: 100%
        threshold: 0%
        if_ci_failed: error

comment:
  layout: "reach,diff,flags,tree"
  behavior: default
  require_changes: false

ignore:
  - "**/*.Generated.cs"
  - "**/.whizbang-generated/**"
  - "**/samples/**"
  - "**/benchmarks/**"
```

#### Add Repository Secret
- [ ] Get Codecov token from https://codecov.io/gh/whizbang-lib/whizbang
- [ ] Add `CODECOV_TOKEN` to GitHub repository secrets

#### Verify GitHub Action
- [ ] Verify `.github/workflows/test.yml` includes Codecov upload (created in section 5.1)
- [ ] Push a commit to test integration
- [ ] Verify coverage report appears on Codecov dashboard

#### Add Badge to README
- [ ] Get badge markdown from Codecov dashboard
- [ ] Add to README.md (will be done in Beta phase, section 3.3)

### 9.3 GitHub Security Features (Alpha)

#### Enable Dependabot Alerts
- [ ] Go to repository Settings → Security & analysis
- [ ] Enable "Dependency graph"
- [ ] Enable "Dependabot alerts"

#### Enable Dependabot Security Updates
- [ ] Enable "Dependabot security updates"
- [ ] Verify `.github/dependabot.yml` exists (created in section 5.1)

#### Enable GitHub Advanced Security
- [ ] Enable "Code scanning" (if available for public repo)
- [ ] Enable "Secret scanning"
- [ ] Enable "Secret scanning push protection"

#### Configure Code Scanning
- [ ] Create `.github/workflows/codeql.yml`:

```yaml
name: "CodeQL"

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main, develop ]
  schedule:
    - cron: '0 0 * * 0'  # Weekly on Sunday

jobs:
  analyze:
    name: Analyze
    runs-on: ubuntu-latest
    permissions:
      actions: read
      contents: read
      security-events: write

    strategy:
      fail-fast: false
      matrix:
        language: [ 'csharp' ]

    steps:
    - name: Checkout repository
      uses: actions/checkout@v4

    - name: Initialize CodeQL
      uses: github/codeql-action/init@v3
      with:
        languages: ${{ matrix.language }}

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'

    - name: Build
      run: dotnet build --configuration Release

    - name: Perform CodeQL Analysis
      uses: github/codeql-action/analyze@v3
```

### 9.4 Document Services (Alpha)

- [ ] Create `.github/SERVICES.md`:

```markdown
# Third-Party Services

This document lists all third-party services used by the Whizbang project.

## Code Quality

### SonarCloud
- **Purpose**: Static code analysis, code quality, and security scanning
- **Free for Open Source**: Yes (unlimited public projects)
- **Dashboard**: https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang
- **Integration**: Automated via GitHub Actions
- **Quality Gate**: 100% coverage, A rating, 0 bugs, 0 vulnerabilities

## Test Coverage

### Codecov
- **Purpose**: Test coverage tracking and reporting
- **Free for Open Source**: Yes
- **Dashboard**: https://codecov.io/gh/whizbang-lib/whizbang
- **Integration**: Automated via GitHub Actions
- **Requirement**: 100% test coverage

## Security

### Dependabot
- **Purpose**: Automated dependency updates and security alerts
- **Free for Open Source**: Yes (GitHub-native)
- **Configuration**: `.github/dependabot.yml`
- **Schedule**: Weekly grouped updates

### GitHub Advanced Security
- **Purpose**: Vulnerability scanning, secret scanning, code scanning
- **Free for Open Source**: Yes (public repositories)
- **Features**:
  - Dependency graph
  - Dependabot alerts
  - Secret scanning
  - CodeQL analysis

## CI/CD

### GitHub Actions
- **Purpose**: Continuous integration and deployment
- **Free for Open Source**: Yes (unlimited minutes for public repos)
- **Workflows**:
  - Build and test
  - Code quality analysis
  - NuGet package publishing
  - CodeQL security scanning

## Package Distribution

### NuGet.org
- **Purpose**: .NET package hosting and distribution
- **Free for Open Source**: Yes
- **Profile**: https://www.nuget.org/profiles/whizbang-lib
- **Packages**: All Whizbang.* packages

## Documentation

### GitHub Pages
- **Purpose**: Documentation website hosting
- **Free for Open Source**: Yes
- **Site**: https://whizbang-lib.github.io
- **Source**: https://github.com/whizbang-lib/whizbang-lib.github.io

## Badges

### Shields.io
- **Purpose**: README badges for build status, coverage, version, etc.
- **Free for Open Source**: Yes (completely free)
- **Service**: https://shields.io

## Setup Instructions

### For New Contributors

All services are pre-configured and will work automatically when you:
1. Fork the repository
2. Create pull requests

### For Maintainers

Required secrets in GitHub repository settings:
- `CODECOV_TOKEN` - Codecov upload token
- `SONAR_TOKEN` - SonarCloud analysis token
- `NUGET_API_KEY` - NuGet.org publishing key

## Cost Summary

**Total Monthly Cost**: $0 (all services free for open source)

## Support

If you encounter issues with any service:
1. Check service status page
2. Review workflow logs in GitHub Actions
3. Open an issue in the repository
```

---

## 10. Legal & Compliance (Alpha)

### 10.1 License Verification

#### Verify LICENSE File
- [ ] Verify `LICENSE` file exists in repository root
- [ ] Confirm license type: MIT
- [ ] Verify copyright year: 2025
- [ ] Verify copyright holder: whizbang-lib

#### Add SPDX Identifier
- [ ] Update LICENSE file to include SPDX identifier:

```
MIT License

SPDX-License-Identifier: MIT

Copyright (c) 2025 whizbang-lib

[Rest of MIT license text...]
```

#### Review Third-Party Licenses
- [ ] Review all packages in `Directory.Packages.props`
- [ ] Verify all dependencies use compatible licenses (MIT, Apache-2.0, BSD, etc.)
- [ ] Document any license compatibility concerns

**Key Packages to Check:**
- Microsoft.* packages (typically MIT)
- Npgsql (PostgreSQL License - permissive)
- Dapper (Apache-2.0)
- TUnit (MIT)
- Rocks (MIT)
- Bogus (MIT)
- Vogen (Apache-2.0)

#### Create NOTICE File (if needed)
- [ ] Determine if NOTICE file is required for attribution
- [ ] If required, create `NOTICE` file:

```
Whizbang
Copyright 2025 whizbang-lib

This product includes software developed by third parties:

[List of third-party components requiring attribution]
```

#### Verify NuGet Package License
- [ ] Verify `PackageLicenseExpression` is set to `MIT` in Directory.Build.props
- [ ] Verify license appears correctly in generated NuGet packages

### 10.2 Copyright Headers Decision

**Decision: NO file-level copyright headers**

**Rationale:**
- Copyright is automatic (exists when code is written)
- MIT license doesn't require file-level headers
- Modern best practice: SPDX identifier in LICENSE file only
- Reduces noise in source files
- Easier to maintain

**What we have instead:**
- [ ] LICENSE file with SPDX identifier at repository root
- [ ] Copyright notice in `Directory.Build.props` for package metadata
- [ ] Copyright notice in CHANGELOG.md and README.md
- [ ] Copyright notice in documentation footer

**Document Decision:**
- [ ] Add to CONTRIBUTING.md:

```markdown
## Copyright and Licensing

### No File-Level Headers Required
This project does not require copyright headers in source files. Copyright is
automatic and the LICENSE file at the repository root applies to all code.

### License
All code is licensed under the MIT License. See LICENSE file for full text.

### Contributions
By contributing, you agree that your contributions will be licensed under the
MIT License.
```

---

## 11. Release Process Documentation (Alpha)

### 11.1 Create Release Checklist

- [ ] Create `.github/RELEASE.md` (this file!)
- [ ] Reference this file from CONTRIBUTING.md
- [ ] Update as we learn from the release process

### 11.2 Create `/release` Slash Command (Wizard)

**Purpose:** Create a comprehensive release wizard that guides through releases AND can generate release plans for future versions.

- [ ] Create `.claude/commands/release.md`:

```markdown
# Release Command - Release Wizard

**A comprehensive wizard for managing Whizbang releases, from planning to execution.**

## Usage

### Prepare a New Release Plan
```bash
/release plan [version]
```

**Example:** `/release plan v0.2.0`

Generates a comprehensive release plan document similar to `plans/v0.1.0-release-plan.md`:
- Creates `plans/v[version]-release-plan.md`
- Based on this template with version-specific updates
- Three-phase structure (Alpha/Beta/GA)
- All standard sections included
- Status tracking checkboxes
- Exit criteria for each phase
- Progress notes section initialized

**What gets customized:**
- Version numbers throughout document
- Dates updated to current
- Previous version references updated
- Empty progress notes for new sessions
- Research findings section ready for new research

**Process:**
1. Ask user for version number if not provided
2. Ask for any specific focus areas for this release (new features, breaking changes, etc.)
3. Generate customized release plan based on this template
4. Add any release-specific sections based on user input
5. Write to `plans/v[version]-release-plan.md`
6. Report success and next steps

### Execute a Release Phase
```bash
/release [alpha|beta|ga] [version]
```

**Example:** `/release alpha v0.1.0`

Guides through executing a release phase using the release plan:
1. Load `plans/v[version]-release-plan.md` (or prompt for version)
2. Identify which phase to execute (alpha/beta/ga)
3. Create TodoWrite tasks for all checklist items in that phase
4. Work through each item systematically:
   - Run commands
   - Perform audits
   - Generate files
   - Execute validations
   - Report results
5. Track progress with TodoWrite
6. Identify and report any blockers
7. Update release plan with progress notes
8. Verify exit criteria before completing
9. Report phase completion status

**Special behaviors:**
- **Warning suppression validation**: Perform web searches to validate wishlist assumptions
- **Error code audit**: Check for new diagnostic codes, validate documentation
- **Absolute path check**: Scan for machine-specific paths
- **StringBuilder audit**: Review generator code for template opportunities
- **Coverage verification**: Enforce 100% test coverage requirement

**Interactive mode:**
- Ask for confirmation before major changes
- Report progress after each section
- Allow user to skip sections if needed
- Resume from last checkpoint if interrupted

### Check Release Status
```bash
/release status [version]
```

**Example:** `/release status v0.1.0`

Reports current release status:
- Which phase is in progress
- Completed checklist items
- Remaining checklist items
- Blockers identified
- Exit criteria status
- Next recommended action

## Process Flow

### Planning Flow
1. User runs `/release plan v0.2.0`
2. Claude generates comprehensive plan document
3. User reviews and customizes as needed
4. Plan becomes the guide for that release

### Execution Flow
1. User runs `/release alpha v0.2.0`
2. Claude loads `plans/v0.2.0-release-plan.md`
3. Claude creates TodoWrite tasks for Alpha phase
4. Claude executes each checklist item:
   - Runs audits (suppressions, StringBuilders, absolute paths)
   - Validates standards compliance
   - Executes web searches for wishlist validation
   - Performs code quality checks
   - Verifies coverage and tests
5. Claude updates progress in release plan
6. Claude reports completion or blockers

### Wishlist Validation (Automated)
For each approved warning suppression with wishlist:
1. Extract wishlist items from documentation
2. Perform current web searches:
   - "C# [current year] [feature] status"
   - ".NET [next version] [feature]"
   - "dotnet runtime [feature] design proposal"
3. Update wishlist document with findings
4. Report if any suppressions can now be removed
5. Update "Last Checked" dates in documentation

## Implementation Notes

**Required capabilities:**
- Read/write release plan files
- Execute TodoWrite for progress tracking
- Run bash commands for audits (grep, etc.)
- Perform web searches for feature validation
- Edit documentation files
- Parse and update markdown checklists
- Validate exit criteria programmatically

**State management:**
- Track which phase is being executed
- Remember last checkpoint for resume
- Store blockers for reporting
- Maintain progress in release plan file

**Error handling:**
- Report clear error messages
- Save progress before failing
- Provide recovery steps
- Don't lose work on interruption

## Example Session

```
User: /release alpha v0.1.0

Claude: Loading release plan for v0.1.0...
Found: plans/v0.1.0-release-plan.md

Starting Alpha Phase execution:
- 11 main sections to complete
- Creating TodoWrite tasks...

Section 1: Repository Hygiene & Organization
[✓] Section 1.1: Clean Root Directory
  - Found 12 files to remove
  - Removed: .DS_Store, baseline-test-results.txt, ...
[✓] Section 1.2: Reorganize Scripts Directory
  - Deduplicated 3 script files
  - Created scripts/README.md
[IN PROGRESS] Section 1.3: Fix Absolute Paths
  - Scanning for absolute paths...
  - Found 5 instances in CLAUDE.md
  - Converting to relative paths...

...

Alpha Phase Status: 8/11 sections complete
Blockers: None
Ready to continue to Section 9: Open Source Services Setup
```
```

- [ ] Implement the release command wizard
- [ ] Test plan generation
- [ ] Test alpha execution
- [ ] Test beta execution
- [ ] Test GA execution
- [ ] Test wishlist validation with web searches
- [ ] Document in CLAUDE.md
- [ ] Add to `.claude/commands/` directory

### 11.3 Document Release Workflow

- [ ] Add to repository documentation:

```markdown
## Release Workflow

### Three-Phase Release Process

Whizbang uses a three-phase release process:

1. **Alpha** - Internal testing and validation
2. **Beta** - Limited public testing with early adopters
3. **GA** - General availability for public use

### Release Checklist

The complete release checklist is maintained in `.github/RELEASE.md`.

### Using the `/release` Command

Claude Code can guide you through the release process:

```bash
/release alpha   # Start alpha release
/release beta    # Start beta release
/release ga      # Start GA release
```

### Manual Release Process

If not using Claude Code, follow these steps:

#### Alpha Release
1. Follow all items in `.github/RELEASE.md` Alpha Phase section
2. Verify all exit criteria are met
3. Tag version: `git tag -a v0.1.0-alpha.1 -m "Alpha 1"`
4. Push tag: `git push origin v0.1.0-alpha.1`
5. GitHub Actions will automatically publish to NuGet

#### Beta Release
1. Complete Alpha phase
2. Address feedback from alpha testing
3. Follow all items in `.github/RELEASE.md` Beta Phase section
4. Tag version: `git tag -a v0.1.0-beta.1 -m "Beta 1"`
5. Push tag: `git push origin v0.1.0-beta.1`

#### GA Release
1. Complete Beta phase
2. Address feedback from beta testing
3. Follow all items in `.github/RELEASE.md` GA Phase section
4. Tag version: `git tag -a v0.1.0 -m "Release v0.1.0"`
5. Push tag: `git push origin v0.1.0`
6. Announce to community

### Version Numbering

See GitVersion section for automatic version calculation.
```

---

## 12. VSCode Extension (Beta Phase, if applicable)

### 12.1 Extension Updates
- [ ] Navigate to `whizbang-vscode` repository
- [ ] Update extension to version compatible with library v0.1.0
- [ ] Test extension with library v0.1.0-alpha
- [ ] Update extension documentation
- [ ] Update package.json version
- [ ] Build extension: `pnpm run compile`
- [ ] Test extension: Press F5 in VSCode
- [ ] Package extension: `pnpm run package`
- [ ] Publish to VSCode marketplace: `pnpm run publish`

### 12.2 Update Library Repository
- [ ] Update `.vscode/extensions.json` in whizbang repo
- [ ] Reference whizbang-vscode extension
- [ ] Document extension in README.md

---

## Execution Phases

### Alpha Phase (Internal Testing) ← **START HERE**

**Goal:** Infrastructure, testing, documentation ready for internal use

**Sections to Complete:**
1. Repository Hygiene & Organization (Section 1)
2. Build Quality - Zero Errors/Warnings (Section 2)
3. Documentation & Standards Verification (Section 3.1, 3.2, 3.5)
4. CLAUDE.md Organization Strategy (Section 4)
5. Build & CI/CD Infrastructure (Section 5)
6. NuGet Package Configuration (Section 6)
7. Quality Assurance - Testing & Code Quality (Section 7.1, 7.2)
8. Documentation Publishing - Library (Section 8.3)
9. Open Source Services Setup (Section 9)
10. Legal & Compliance (Section 10)
11. Release Process Documentation (Section 11)

**Exit Criteria:**
- [ ] 0 errors in build
- [ ] 0 warnings in build
- [ ] 100% test coverage achieved
- [ ] All CI/CD pipelines configured and passing
- [ ] All services (SonarCloud, Codecov, etc.) configured and working
- [ ] Internal testing complete
- [ ] All documentation complete and accurate
- [ ] All scripts organized and documented
- [ ] Repository clean and organized
- [ ] All absolute paths converted to relative or generic examples
- [ ] All generators verified to follow template/snippet guidelines
- [ ] StringBuilder usage audited and documented/refactored as needed
- [ ] All error codes organized into categories by 100s
- [ ] All error codes documented with examples in documentation site
- [ ] All warning suppressions audited, documented, and have wishlists
- [ ] Wishlist assumptions validated with current web searches
- [ ] Zero unapproved suppressions in production code

**Deliverable:** `v0.1.0-alpha.1` published to NuGet

---

### Beta Phase (Limited Public Testing)

**Goal:** Real-world validation with early adopters

**Prerequisites:**
- Alpha phase complete
- All alpha exit criteria met

**Additional Sections to Complete:**
1. Update README with badges and installation (Section 3.3)
2. CLI Documentation (Section 3.4)
3. Performance Testing (Section 7.3)
4. Documentation Site Publishing (Section 8.1)
5. VSCode Extension Updates (Section 12)

**Activities:**
- [ ] Publish alpha release to NuGet
- [ ] Share with select early adopters
- [ ] Gather feedback via GitHub Discussions
- [ ] Monitor for issues and bugs
- [ ] Address critical issues
- [ ] Validate performance
- [ ] Complete documentation site

**Exit Criteria:**
- [ ] Beta feedback addressed
- [ ] Performance validated and documented
- [ ] Documentation site published and complete
- [ ] CLI documentation ready
- [ ] VSCode extension compatible and published
- [ ] No critical bugs remaining
- [ ] README updated with installation and badges

**Deliverable:** `v0.1.0-beta.1` published to NuGet

---

### GA Phase (General Availability)

**Goal:** Public release to .NET community

**Prerequisites:**
- Beta phase complete
- All beta exit criteria met

**Additional Sections to Complete:**
1. Blog Section (Section 8.2)
2. Community Announcements

**Activities:**
- [ ] Create release blog post
- [ ] Finalize release notes in CHANGELOG.md
- [ ] Update all version references to stable
- [ ] Create GitHub release with notes
- [ ] Publish stable version to NuGet

**Community Announcements (by Phil):**
- [ ] Publish blog post on documentation site
- [ ] Post announcement in Discord channel
- [ ] Post to Reddit r/dotnet
- [ ] Post to Twitter/X
- [ ] Post to LinkedIn
- [ ] Consider: Hacker News, dev.to

**Exit Criteria:**
- [ ] All critical and high-priority issues resolved
- [ ] Blog post published
- [ ] Community announcements sent
- [ ] Release notes finalized
- [ ] GitHub release created
- [ ] Stable version published to NuGet

**Deliverable:** `v0.1.0` (stable) published to NuGet

---

## Research Findings

### Third-Party Services Investigation

#### SonarCloud
**Status:** ✅ RECOMMENDED
**Cost:** FREE for open source (unlimited public projects)
**Limits:** Private projects limited to 50K LOC on free tier
**Features:** Full features for public repos including quality gates, security analysis, code smells detection
**Decision:** Use SonarCloud for comprehensive code quality analysis

#### Snyk
**Status:** ❌ NOT RECOMMENDED
**Cost:** Limited free tier
**Limits:**
- 400 tests/month for Open Source scanning
- Scope reduced to "1-2 code bases"
- Missing features: reporting over time, SSO, advanced scanning
- Weekly-only recurring scans for private repos

**Decision:** Skip Snyk, use Dependabot + GitHub Advanced Security instead

#### Shields.io
**Status:** ✅ RECOMMENDED
**Cost:** Completely FREE
**Details:** Open source project, serves 1.6B+ images/month, community-supported
**Decision:** Use Shields.io for all README badges

#### CircleCI
**Status:** ❌ NOT NEEDED
**Reason:** GitHub Actions provides better integration and is free for public repos
**Decision:** Use GitHub Actions exclusively

---

## Open Questions - ANSWERED

### Q1: Third-party services - Which are confirmed free for OSS?
**Answer:** See Research Findings section above. Using: SonarCloud, Codecov, GitHub Actions, Dependabot, Shields.io, GitHub Advanced Security.

### Q2: Copyright headers - Is that normal for OSS?
**Answer:** NO - not required and increasingly uncommon.
- Copyright is automatic (exists when code is written)
- MIT license doesn't require file-level headers
- Modern best practice (Linux Foundation): Use SPDX tags in LICENSE file only
- **Decision:** Skip file-level headers, maintain LICENSE file with SPDX identifier

### Q3: Package icon - Who will design it?
**Answer:** Phil will provide the icon. Icon already exists in docs repo, will be copied to library repo.

### Q4: Blog post hosting - Where to host?
**Answer:** GitHub Pages with Jekyll (FREE)
- Add `/blog` section to existing `whizbang-lib.github.io` site
- Jekyll is built into GitHub Pages (blog-aware static site generator)
- Perfect for OSS project announcements
- **Decision:** Add blog to docs site under `src/assets/blog/`

### Q5: Social media - Who will post?
**Answer:** Phil will handle all social media announcements and Discord channel posts.

---

## Progress Notes

### Session: 2024-12-22 (Initial Plan Creation)
- Created comprehensive release plan
- Researched third-party services
- Made key decisions on copyright headers, blog hosting, and services
- Plan status: Ready for Alpha Phase execution

### Session: 2024-12-22 (Update 1)
- Added Section 1.3: Fix Absolute Paths
- Requirement: All local absolute paths must be converted to relative or generic examples
- Updated Alpha Phase exit criteria to include absolute path verification
- Rationale: Ensure codebase and documentation are portable across different developer machines

### Session: 2024-12-22 (Update 2)
- Added **Source Generator Standards** verification to Section 3.1
  - Verify generators follow template/snippet guidelines
  - Check syntactic filtering patterns
  - Verify sealed records usage (not classes)
  - Verify no cross-dependencies between generators
- Added **StringBuilder Usage Audit** with detailed guidelines
  - When StringBuilder is acceptable (small, simple generation)
  - When to use Templates/Snippets instead (large blocks, structured code)
  - Audit checklist with grep command
  - Examples of good vs. bad StringBuilder usage
- Added **Error Code Organization and Documentation** to Section 3.1
  - Organize error codes into categories by 100s (100-199, 200-299, etc.)
  - Rename codes if needed to fit categorical scheme
  - Add `<docs>` tags to all DiagnosticDescriptor declarations
  - Document each error code in documentation site with:
    - What it means, common causes, how to fix
    - Code examples showing error and fix
  - Create `docs/diagnostics/error-codes.md` page
  - Regenerate code-docs mapping
  - Validate all diagnostic documentation links
- Rationale: Ensure generator code quality, maintainability, and comprehensive error documentation for users

### Session: 2024-12-22 (Update 3)
- Added **Section 2.4: Warning Suppression Audit** - CRITICAL for AOT compliance
  - Find all `#pragma warning disable` and `SuppressMessage` attributes
  - Audit each suppression: Is it in appropriate code? Is it documented? Is it approved?
  - **BLOCK RELEASE** if production code has unapproved AOT warning suppressions
  - Create wishlist documentation system (`docs/wishlists/`)
  - Wishlist structure: Current limitation, desired C# features, likelihood assessment
  - Web search validation for wishlist items on each release:
    - Check if features are now available
    - Update status (proposed/preview/released)
    - Remove suppressions when features become available
  - Categories: Approved (tests, generators with docs) vs. Never Approved (production, convenience, undocumented)
  - Example documentation templates provided
  - Grep commands for finding suppressions
- Expanded **Section 11.2: `/release` Command** from simple script to comprehensive wizard
  - **Plan generation**: `/release plan [version]` creates customized release plan documents for future versions
  - **Phase execution**: `/release [alpha|beta|ga] [version]` guides through execution with TodoWrite
  - **Status checking**: `/release status [version]` reports progress
  - Automated wishlist validation with web searches
  - Interactive mode with progress tracking
  - State management and error handling
  - Resume capability for interrupted releases
  - Implementation requirements and example session provided
- Updated Alpha Phase exit criteria to include:
  - All warning suppressions audited, documented, and have wishlists
  - Wishlist assumptions validated with current web searches
  - Zero unapproved suppressions in production code
- Rationale:
  - Strict AOT compliance is non-negotiable for Whizbang
  - Suppressions must be rare, documented, and tracked for future removal
  - Web search validation ensures we stay current with C# evolution
  - `/release` wizard will streamline all future releases and ensure consistency

---

## Next Steps

1. **Review this plan** - Ensure completeness and accuracy
2. **Begin Alpha Phase** - Start with Section 1: Repository Hygiene
3. **Use `/release alpha`** - Let Claude guide through each step
4. **Track progress** - Check off items as completed
5. **Update plan** - Adjust as we learn from the process
6. **Document learnings** - Add notes for future releases

---

**Remember:** This is a living document. Update it as we progress through the release!
