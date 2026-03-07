# Git Flow Branching Strategy

This project follows a **Git Flow** branching model. All contributors must follow these rules to maintain a clean and predictable release process.

## Branch Types

| Branch | Purpose | Created From | Merges Into |
|--------|---------|--------------|-------------|
| `main` | Production-ready code | - | - |
| `develop` | Integration branch for features | `main` | `main` (via release) |
| `feature/*` | New features | `develop` | `develop` |
| `release/*` | Release preparation | `develop` | `main` |
| `hotfix/*` | Urgent production fixes | `main` | `main` AND `develop` |

```
main ─────●─────────────────────●─────────────────●─────
          │                     ↑                 ↑
          │                     │                 │
          ▼                     │                 │
develop ──●───●───●───●─────────●─────●───●───────●─────
              │   ↑   │         ↑     │   ↑       ↑
              │   │   │         │     │   │       │
              ▼   │   ▼         │     ▼   │       │
feature/*     ●───●   ●─────────┘     ●───●       │
                                                  │
release/*                 ●───────────────────────●
```

## Allowed PR Directions

### Feature Development
```
feature/* → develop    ✅ ALLOWED
feature/* → main       ❌ BLOCKED - Features must go through develop
feature/* → release/*  ❌ BLOCKED - Features don't go into releases
```

### Release Process
```
develop → release/*    ✅ ALLOWED (via start-release.yml workflow)
release/* → main       ✅ ALLOWED - Release PRs merge to main
release/* → develop    ❌ BLOCKED - Releases don't merge back to develop directly
```

### Hotfixes
```
hotfix/* → main        ✅ ALLOWED - Hotfixes go directly to production
hotfix/* → develop     ✅ ALLOWED - Hotfixes also merge to develop
main → develop         ✅ ALLOWED - Automated sync after release
```

### Synchronization
```
main → develop         ✅ ALLOWED - Automated after releases
develop → main         ❌ BLOCKED - Use release/* branches instead
```

## Quick Reference

### I want to add a new feature
1. Create branch from `develop`: `git checkout -b feature/my-feature develop`
2. Make your changes
3. Create PR: `feature/my-feature → develop`

### I want to release
1. Run `start-release.yml` workflow from `develop`
2. Workflow creates `release/vX.Y.Z` branch and PR to `main`
3. Review and merge the PR
4. Main automatically syncs back to develop

### I have an urgent production fix
1. Create branch from `main`: `git checkout -b hotfix/fix-name main`
2. Make your fix
3. Create PR: `hotfix/fix-name → main`
4. After merge, also create PR: `hotfix/fix-name → develop` (or wait for auto-sync)

## Enforcement

This repository has automated checks that validate PR branch directions:

- PRs violating these rules will be blocked with a clear error message
- The error will explain what went wrong and link to this documentation
- Force pushes to `main` and `develop` are disabled

## Why Git Flow?

1. **Clean history**: Main always reflects production state
2. **Safe releases**: Release branches allow final testing without blocking feature work
3. **Parallel development**: Multiple features can be developed simultaneously
4. **Hotfix path**: Critical fixes don't need to wait for the next release cycle

## Common Mistakes

### ❌ "I accidentally branched from main instead of develop"

```bash
# Fix: Rebase your feature onto develop
git checkout feature/my-feature
git rebase --onto develop main feature/my-feature
git push --force-with-lease
```

### ❌ "I want to merge develop directly to main"

Don't do this! Use the release workflow instead:
1. Go to **Actions** → **Start Release**
2. Run the workflow with your desired version

### ❌ "My PR is blocked but I think it's correct"

Check the error message - it will tell you:
- What branches are involved
- What the expected direction should be
- A link to this documentation

If you believe the check is wrong, contact a maintainer.

## Related Documentation

- [WORKFLOWS.md](./WORKFLOWS.md) - CI/CD pipeline documentation
- [CONTRIBUTING.md](./CONTRIBUTING.md) - Contribution guidelines
