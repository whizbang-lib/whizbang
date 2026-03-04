# AI-Focused Documentation

**Comprehensive guidance for Claude Code when working with Whizbang library**

This directory contains focused documentation topics to help Claude Code understand and apply Whizbang standards without loading unnecessary context.

---

## 📚 Quick Navigation

### 🧪 Testing & Development
- **[testing-tunit.md](testing-tunit.md)** - TUnit CLI, Rocks mocking, Bogus fake data
  - **When to use:** Running tests, mocking dependencies, generating test data
  - **Critical:** `--treenode-filter` not `--filter`, coverage with `dotnet run`

- **[tdd-strict.md](tdd-strict.md)** - RED/GREEN/REFACTOR cycle
  - **When to use:** Writing new features, fixing bugs
  - **Critical:** Test-first is mandatory, 100% coverage goal

- **[flaky-tests.md](flaky-tests.md)** - Diagnosing and fixing intermittent test failures
  - **When to use:** Tests pass sometimes but fail other times
  - **Critical:** Static resources need `[NotInParallel]`, wait for ALL events affecting asserted data

### 💻 Code Quality
- **[code-standards.md](code-standards.md)** - Formatting, naming, quality
  - **When to use:** Writing any C# code
  - **Critical:** `dotnet format` before every commit, async methods end with "Async"

- **[documentation-maintenance.md](documentation-maintenance.md)** - Keeping docs synchronized with code
  - **When to use:** Changing public APIs in ANY project (Core, Generators, Testing)
  - **Critical:** ALWAYS ask version first, update docs when changing public APIs

- **[boy-scout-rule.md](boy-scout-rule.md)** - Leave code better than you found it
  - **When to use:** REFACTOR phase, discovering issues
  - **Critical:** No "that was pre-existing" excuses

### 🏗️ Architecture
- **[aot-requirements.md](aot-requirements.md)** - Zero reflection, native AOT
  - **When to use:** Writing library code, sample projects
  - **Critical:** Library = STRICT AOT, Samples = STRICT AOT, Tests = preferred

- **[sample-projects.md](sample-projects.md)** - Dogfooding standards
  - **When to use:** Working on ECommerce or other samples
  - **Critical:** When sample needs feature → implement in library first

### 🛠️ Tools & Infrastructure
- **[efcore-10-usage.md](efcore-10-usage.md)** - JsonB, UUIDv7, complex types
  - **When to use:** Database operations, entity configuration
  - **Critical:** Use `Guid.CreateVersion7()`, complex types not owned entities

- **[script-standards.md](script-standards.md)** - PowerShell, containers
  - **When to use:** Writing scripts, organizing /scripts/ folder
  - **Critical:** Prefer `.ps1` (PowerShell Core), multi-platform required

---

## 🎯 Common Scenarios

### "I'm writing a new feature"
1. Read: [tdd-strict.md](tdd-strict.md) - RED/GREEN/REFACTOR
2. Read: [testing-tunit.md](testing-tunit.md) - How to write tests
3. Read: [code-standards.md](code-standards.md) - Formatting rules
4. Read: [boy-scout-rule.md](boy-scout-rule.md) - REFACTOR phase
5. Run: `dotnet format` before completion

### "I'm changing a public API" (ANY project: Core, Generators, Testing)
1. Read: [documentation-maintenance.md](documentation-maintenance.md) - Complete workflow
2. Ask: "What version are you working on?" (MANDATORY)
3. Add/update `<docs>` tag in source code
4. Update documentation in `whizbang-lib.github.io` repo
5. Regenerate code-docs mapping
6. Validate links with MCP tool
7. Commit both repos

### "I'm adding tests"
1. Read: [testing-tunit.md](testing-tunit.md) - TUnit patterns
2. Read: [tdd-strict.md](tdd-strict.md) - Test-first rule
3. Use: `dotnet run -- --treenode-filter "/*/*/MyTestClass/*"`
4. Use: Rocks for mocking, Bogus for test data
5. Remember: ALL async tests end with "Async"

### "I'm working on a sample project"
1. Read: [sample-projects.md](sample-projects.md) - Dogfooding rules
2. Read: [aot-requirements.md](aot-requirements.md) - STRICT AOT
3. If sample needs feature → STOP and implement in library
4. Never create workarounds in samples

### "I'm using EF Core / PostgreSQL"
1. Read: [efcore-10-usage.md](efcore-10-usage.md) - JsonB, UUIDv7
2. Use: Complex types with `ToJson()` (not owned entities)
3. Use: `Guid.CreateVersion7()` for all IDs
4. Use: Partial JSON updates with `ExecuteUpdateAsync`

### "I discovered tech debt"
1. Read: [boy-scout-rule.md](boy-scout-rule.md) - Required behavior
2. Update working plan with discovered issue
3. Fix root cause, not symptom
4. No "that's out of scope" excuses

### "I'm running tests or coverage"
1. Read: [testing-tunit.md](testing-tunit.md) - CLI commands
2. Use: `dotnet run` not `dotnet test` for coverage
3. Use: `--treenode-filter` not `--filter`
4. Use: `--coverage --coverage-output-format cobertura`

### "Tests are flaky (pass sometimes, fail sometimes)"
1. Read: [flaky-tests.md](flaky-tests.md) - Common patterns and fixes
2. Check: Is it a static shared resource? → Add `[NotInParallel]`
3. Check: Missing event waiter? → Wait for ALL events that affect asserted data
4. Check: Timeout too short? → Increase for bulk operations (200s) or parallel load (600ms delays)
5. Check: Timing-sensitive? → Use deterministic synchronization instead of delays

### "JSON serialization error (DateTimeOffset, enum, type not found)"
1. Read: [json-serialization-customizations.md](json-serialization-customizations.md) - All custom handling
2. Check: Is it a PostgreSQL edge case? (`-infinity`, no timezone, etc.)
3. Check: Is it a nullable enum? → Generator should create both versions
4. Check: Is it a nested type? → May need CLR name format handling
5. Add: Test case and handling to appropriate converter

---

## 📖 Complete Documentation List

### Core Standards
1. **[code-standards.md](code-standards.md)** - Formatting, naming, dotnet format (MANDATORY)
2. **[tdd-strict.md](tdd-strict.md)** - Test-driven development (RED/GREEN/REFACTOR)
3. **[documentation-maintenance.md](documentation-maintenance.md)** - Keeping docs synchronized with code (CRITICAL)
4. **[boy-scout-rule.md](boy-scout-rule.md)** - Always improve code
5. **[aot-requirements.md](aot-requirements.md)** - Zero reflection, native AOT

### Development Practices
6. **[testing-tunit.md](testing-tunit.md)** - TUnit, Rocks, Bogus usage
7. **[flaky-tests.md](flaky-tests.md)** - Diagnosing and fixing intermittent test failures
8. **[sample-projects.md](sample-projects.md)** - Dogfooding philosophy
9. **[efcore-10-usage.md](efcore-10-usage.md)** - PostgreSQL JsonB, UUIDv7
10. **[script-standards.md](script-standards.md)** - PowerShell, containers

### Internals & Troubleshooting
11. **[json-serialization-customizations.md](json-serialization-customizations.md)** - Custom JSON converters and edge cases
    - **When to use:** Debugging serialization errors, adding new converters
    - **Critical:** Documents PostgreSQL-specific handling (infinity, no timezone, etc.)

---

## 🔑 Key Principles (Always Remember)

### 1. Test-Driven Development
- ✅ Tests BEFORE implementation (no exceptions)
- ✅ RED → GREEN → REFACTOR cycle
- ✅ 100% branch coverage goal

### 2. Code Quality
- ✅ `dotnet format` before EVERY commit
- ✅ ALL async methods end with "Async"
- ✅ Boy Scout Rule in REFACTOR phase

### 3. Documentation Maintenance
- ✅ ALWAYS ask "What version are you working on?" first
- ✅ Update docs when changing public APIs (Core, Generators, Testing, ALL projects)
- ✅ Same version = update in place, Next version = create new/use drafts
- ✅ Commit before deletions for safety

### 4. AOT Compatibility
- ✅ Library: ZERO reflection (absolute)
- ✅ Samples: ZERO reflection (absolute)
- ✅ Tests: Preferred but not required

### 5. Sample Projects
- ✅ Samples dogfood the library
- ✅ When sample needs feature → library implements it
- ✅ NO workarounds in samples

### 6. Database
- ✅ Use `Guid.CreateVersion7()` for all IDs
- ✅ Complex types with `ToJson()` for JsonB
- ✅ Partial updates with `ExecuteUpdateAsync`

### 7. Scripts
- ✅ Prefer PowerShell Core (`.ps1`)
- ✅ Multi-platform required
- ✅ Containers for tools

---

## 🚫 Common Mistakes to Avoid

### ❌ NEVER Do These

1. **Write implementation before tests**
   - Violates TDD, fails code review

2. **Skip `dotnet format`**
   - Breaks CI/CD, wastes time

3. **Use `Guid.NewGuid()` for IDs**
   - Use `Guid.CreateVersion7()` instead

4. **Use `--filter` with TUnit**
   - Use `--treenode-filter` instead

5. **Forget "Async" suffix**
   - ALL async methods need it (even tests)

6. **Create workarounds in samples**
   - Implement feature in library first

7. **Change public APIs without updating documentation**
   - ALWAYS ask version first, update docs

8. **Say "that was pre-existing"**
   - Boy Scout Rule: fix it anyway

9. **Accept low test coverage**
   - Goal is 100%, keep working toward it

10. **Use owned entities in EF Core 10**
    - Use complex types instead

11. **Use bash without reason**
    - Prefer PowerShell Core

---

## 📌 Quick Reference Cards

### Running Tests
```bash
# All tests
dotnet test

# Specific class
dotnet run -- --treenode-filter "/*/*/OrderServiceTests/*"

# With coverage
cd tests/Whizbang.Core.Tests
dotnet run -- --coverage --coverage-output-format cobertura
```

### Before Every Commit
```bash
# 1. Format code (MANDATORY)
dotnet format

# 2. Run tests
dotnet test

# 3. Check coverage
dotnet run -- --coverage

# 4. Verify Boy Scout Rule applied
# Did I make code better than I found it?
```

### Creating Entities
```csharp
// ✅ CORRECT
var order = new Order {
    Id = OrderId.From(Guid.CreateVersion7()),  // UUIDv7!
    // ...
};

// ❌ WRONG
var order = new Order {
    Id = OrderId.From(Guid.NewGuid()),  // Random GUID
    // ...
};
```

---

## 🔗 Related Documentation

### In This Repo
- `/CLAUDE.md` - Library repo overview
- `/TESTING.md` - Testing infrastructure
- `/src/Whizbang.Generators/CLAUDE.md` - Source generator guidance

### Other Repos
- `../../CLAUDE.md` - Workspace overview
- `../../whizbang-lib.github.io/ai-docs/` - Documentation site standards

---

## 📝 Document Maintenance

### When to Update These Docs

**Add new documentation when:**
- New critical pattern emerges
- Common mistakes need documenting
- New technology is adopted
- Standards change

**Update existing documentation when:**
- Rules change
- Better examples found
- Mistakes are discovered
- Tooling is updated

**Follow Boy Scout Rule:**
- Fix typos when you see them
- Improve examples
- Add missing clarifications
- Keep docs current

---

## 💡 Tips for Claude Code

### Context Loading Strategy

**Don't load all docs at once!** Load only what's needed:

**For feature development:**
- Load: tdd-strict.md, code-standards.md, testing-tunit.md
- Skip: script-standards.md, sample-projects.md

**For sample work:**
- Load: sample-projects.md, aot-requirements.md
- Skip: efcore-10-usage.md (unless using database)

**For database work:**
- Load: efcore-10-usage.md, aot-requirements.md
- Skip: testing-tunit.md, script-standards.md

**For script creation:**
- Load: script-standards.md
- Skip: testing-tunit.md, efcore-10-usage.md

### Cross-References

These docs reference each other. When one mentions another:
- **"See Also"** sections link related topics
- Load referenced docs when deeper understanding needed
- Don't load entire dependency tree upfront

---

## ✅ Success Checklist

Before claiming work complete:

- [ ] Tests written first (TDD)
- [ ] All tests passing
- [ ] `dotnet format` run
- [ ] 100% branch coverage achieved (or working toward it)
- [ ] Async methods end with "Async"
- [ ] UUIDs use `Guid.CreateVersion7()`
- [ ] Boy Scout Rule applied
- [ ] Documentation updated (if public APIs changed in ANY project)
- [ ] Version determined and correct strategy used (same vs. next)
- [ ] Code-docs mapping regenerated (if `<docs>` tags changed)
- [ ] Links validated (if documentation changed)
- [ ] No workarounds in samples
- [ ] AOT compatibility verified

---

## 🎓 Learning Path

**New to Whizbang? Read in this order:**

1. **[tdd-strict.md](tdd-strict.md)** - Understand TDD requirement
2. **[code-standards.md](code-standards.md)** - Learn formatting rules
3. **[testing-tunit.md](testing-tunit.md)** - Master testing tools
4. **[boy-scout-rule.md](boy-scout-rule.md)** - Adopt continuous improvement mindset
5. **[aot-requirements.md](aot-requirements.md)** - Understand AOT constraints
6. **Other docs as needed** - Reference-based learning

---

**Remember:** These docs exist to help you work effectively. Use them as references, not as context you must load every time. Load what you need, when you need it.
