# Contributing to Whizbang

Thank you for your interest in contributing to Whizbang! This document provides guidelines and instructions for contributing.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Workflow](#development-workflow)
- [Standards and Guidelines](#standards-and-guidelines)
- [Submitting Changes](#submitting-changes)
- [Community](#community)

## Code of Conduct

This project adheres to the Contributor Covenant [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.

## Getting Started

### Prerequisites

- .NET 10 SDK
- PowerShell 7+ (cross-platform)
- Git

For detailed setup instructions, see [.github/SETUP.md](.github/SETUP.md).

### Setting Up Your Development Environment

1. **Fork and clone the repository**

```bash
git clone https://github.com/YOUR-USERNAME/whizbang.git
cd whizbang
```

2. **Build the solution**

```bash
dotnet build
```

3. **Run tests**

```bash
pwsh scripts/Run-Tests.ps1
```

## Development Workflow

Whizbang follows a **documentation-first, test-driven development** approach:

1. **Document First** - Create or update documentation in the [whizbang-lib.github.io](https://github.com/whizbang-lib/whizbang-lib.github.io) repository
2. **Test Second** - Write tests that define the behavior (RED → GREEN → REFACTOR)
3. **Implement Third** - Make the tests pass
4. **Format Code** - Run `dotnet format` before committing

### Branch Strategy

- `main` - Stable release branch
- `develop` - Active development branch
- Feature branches: `feature/feature-name`
- Bug fix branches: `fix/issue-description`

### Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
type(scope): subject

body (optional)

footer (optional)
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `test`: Test changes
- `refactor`: Code refactoring
- `perf`: Performance improvements
- `chore`: Build/tooling changes

**Examples:**
```
feat(dispatcher): Add batch message dispatching
fix(receptor): Handle null message gracefully
docs(readme): Update installation instructions
test(core): Add tests for message validation
```

## Standards and Guidelines

### Code Standards

- **Formatting**: K&R/Egyptian braces (opening brace on same line)
- **Async Naming**: All async methods must end with `Async` suffix
- **Documentation**: XML documentation required for all public APIs
- **AOT Compatibility**: Zero reflection in production code (use source generators)

See [ai-docs/code-standards.md](ai-docs/code-standards.md) for complete guidelines.

### Testing Standards

- **TUnit** for test framework with Microsoft.Testing.Platform
- **Rocks** for AOT-compatible mocking
- **Bogus** for test data generation
- All test methods must end with `Async` suffix
- Follow RED → GREEN → REFACTOR cycle

See [ai-docs/testing-tunit.md](ai-docs/testing-tunit.md) and [ai-docs/tdd-strict.md](ai-docs/tdd-strict.md).

### Source Generator Standards

- Use templates/snippets over StringBuilder for code generation
- Follow syntactic filtering patterns (predicate before transform)
- Use sealed records for cache-able data structures
- No cross-dependencies between generators

See [src/Whizbang.Generators/ai-docs/](src/Whizbang.Generators/ai-docs/) for detailed guidelines.

### Boy Scout Rule

**Leave code better than you found it.**

When touching existing code:
- Fix nearby style violations
- Add missing documentation
- Improve variable names
- Simplify complex logic
- Remove dead code

See [ai-docs/boy-scout-rule.md](ai-docs/boy-scout-rule.md).

## Submitting Changes

### Pull Request Process

1. **Create a feature branch**

```bash
git checkout -b feature/my-feature
```

2. **Make your changes**
   - Document first (in whizbang-lib.github.io)
   - Write tests
   - Implement feature
   - Run `dotnet format`

3. **Run quality checks**

```bash
# Build
dotnet build

# Run tests
pwsh scripts/Run-Tests.ps1

# Format code
dotnet format

# Verify no changes needed
dotnet format --verify-no-changes
```

4. **Commit your changes**

```bash
git add .
git commit -m "feat(scope): description"
```

5. **Push to your fork**

```bash
git push origin feature/my-feature
```

6. **Create Pull Request**
   - Go to GitHub and create a PR from your fork
   - Fill out the PR template
   - Link related issues
   - Wait for CI checks to pass

### Pull Request Requirements

- ✅ All tests pass
- ✅ Code coverage maintained or improved
- ✅ `dotnet format` passes with no changes
- ✅ Documentation updated
- ✅ No compiler warnings
- ✅ No AOT warnings in production code
- ✅ Commit messages follow Conventional Commits

### Code Review Process

1. Automated checks run via GitHub Actions
2. Maintainers review code and documentation
3. Address feedback and update PR
4. Once approved, maintainer merges PR

## Release Process

For maintainers preparing releases, see [.github/RELEASE.md](.github/RELEASE.md) for the complete release checklist covering:
- Alpha, Beta, and GA release phases
- Exit criteria for each phase
- Quality gates and validation steps
- Publishing workflow

## Community

### Getting Help

- **Documentation**: https://whizbang-lib.github.io
- **Discussions**: https://github.com/whizbang-lib/whizbang/discussions
- **Issues**: https://github.com/whizbang-lib/whizbang/issues

### Reporting Bugs

1. Check existing issues first
2. Create new issue with template
3. Include:
   - .NET version
   - Operating system
   - Minimal reproduction
   - Expected vs actual behavior

### Suggesting Features

1. Open a discussion first to gauge interest
2. If there's support, create feature request issue
3. Include:
   - Use case and motivation
   - Proposed API design
   - Example usage
   - Alternatives considered

### Recognition

Contributors are recognized through:
- [CONTRIBUTORS.md](CONTRIBUTORS.md) file
- Release notes for significant contributions
- Credit in documentation
- Mentions in blog posts for major features

## Development Services

### CI/CD

- **GitHub Actions** for build/test automation
- **SonarCloud** for code quality analysis
- **Codecov** for test coverage tracking
- **Dependabot** for dependency updates

### Local Development Tools

- **scripts/Run-Tests.ps1** - Run tests with various options
- **scripts/coverage/** - Generate coverage reports
- **scripts/diagnostics/** - View generator diagnostics
- **scripts/benchmarks/** - Run performance benchmarks

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

## Questions?

Feel free to ask questions in [GitHub Discussions](https://github.com/whizbang-lib/whizbang/discussions).

Thank you for contributing to Whizbang!
