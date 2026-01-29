# Release Command - Release Wizard

**A comprehensive wizard for managing Whizbang releases, from planning to execution.**

## Usage

### Quick Version Bump (Automated)
```bash
/release [major|minor|patch|auto]
```

**Examples:**
- `/release minor` - Bump minor version (0.x.0)
- `/release major` - Bump major version (x.0.0)
- `/release patch` - Bump patch version (0.0.x)
- `/release auto` - Let GitVersion calculate from commits

**What happens:**
1. Triggers the GitHub Actions release workflow
2. GitVersion calculates the new version
3. Updates `Directory.Build.props` automatically
4. Creates git tag (e.g., `v0.2.0`)
5. Creates GitHub Release
6. Triggers NuGet publish

**Implementation:**
When user runs `/release minor`, execute:
```bash
gh workflow run release.yml -f release_type=minor
```

Then monitor the workflow and report progress.

---

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

## Merge Strategy

When merging PRs during release workflows, follow these conventions:

| Merge Type | Strategy | Reason |
|------------|----------|--------|
| **feature → develop** | **Squash merge** | Clean atomic commits, hides iterative development |
| **bugfix → develop** | **Squash merge** | Clean atomic commits |
| **develop → main (release)** | **Squash merge** | Single clean release commit |
| **hotfix → main/develop** | **Merge commit** | Preserves audit trail for critical fixes |

**Default to squash merge** for all PRs except hotfixes. This keeps branch history clean and meaningful.

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

## Automated Version Management

The project uses **GitVersion** with **SemVer 2.0** and **GitFlow** branching for automated version calculation.

### Version Bump Commands
```bash
# Auto-calculate version based on branch and commits
gh workflow run release.yml -f release_type=auto

# Force specific version bump
gh workflow run release.yml -f release_type=major  # x.0.0
gh workflow run release.yml -f release_type=minor  # 0.x.0
gh workflow run release.yml -f release_type=patch  # 0.0.x

# Dry run (preview version without creating tag)
gh workflow run release.yml -f release_type=auto -f dry_run=true
```

### GitFlow Branch Versioning

| Branch | Version Format | Example |
|--------|---------------|---------|
| `main` | Stable release | `0.2.0` |
| `develop` | Alpha pre-release | `0.3.0-alpha.1` |
| `release/*` | Beta pre-release | `0.2.0-beta.1` |
| `feature/*` | Feature pre-release | `0.3.0-feat-xyz.1` |
| `hotfix/*` | Hotfix pre-release | `0.2.1-hotfix.1` |

### Conventional Commits for Version Bumps

GitVersion uses conventional commits to determine version increments:

- **feat:** commits trigger a Minor version bump (e.g., `feat: add new dispatcher`)
- **fix:** commits trigger a Patch version bump (e.g., `fix: resolve null reference`)
- **BREAKING CHANGE:** or commits with **!** trigger a Major version bump (e.g., `feat!: redesign API`)

### Release Workflow

The release workflow (`release.yml`) performs these steps:
1. Calculate version using GitVersion
2. Update `Directory.Build.props` with new version
3. Commit the version change
4. Create and push git tag (e.g., `v0.2.0`)
5. Create GitHub Release (stable) or Pre-Release (alpha/beta)
6. Trigger NuGet publish workflow via tag

### Practical Release Workflow

**Important:** The `/release` command is run AFTER merging to the appropriate branch. The branch determines the version type (alpha/beta/stable).

#### Want an Alpha Release?
```bash
# 1. Merge your PR to develop
gh pr merge 43 --squash

# 2. Checkout develop and trigger release
git checkout develop && git pull
/release auto
# → Creates: 0.3.0-alpha.1
```

#### Want a Beta Release?
```bash
# 1. Create release branch from develop
git checkout develop
git checkout -b release/0.2.0
git push -u origin release/0.2.0

# 2. Trigger release from release branch
/release auto
# → Creates: 0.2.0-beta.1
```

#### Want a Stable Release?
```bash
# 1. Merge release branch to main
git checkout main
git merge release/0.2.0
git push

# 2. Trigger release from main
/release auto  # or /release minor
# → Creates: 0.2.0
```

**TL;DR:**
- PR builds get `prXX.Y` versions (CI only, not published)
- Merge to `develop` → alpha
- Create `release/*` branch → beta
- Merge to `main` → stable release

### Quick Release Checklist

For a new release:
1. Ensure all PRs are merged to `main`
2. Run `/release-check` to verify readiness
3. Run: `gh workflow run release.yml -f release_type=minor`
4. Monitor the workflow in GitHub Actions
5. Verify NuGet packages are published

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
