# Source Generators - AI Documentation

**Focused guidance for developing Roslyn source generators in Whizbang**

This directory contains topic-specific documentation for working with Whizbang's source generators. Load only what you need for your current task.

---

## 📚 Quick Navigation

### 🏗️ Understanding the System
- **[architecture.md](architecture.md)** - Multiple Independent Generators Pattern
  - **When to use:** Understanding overall generator design
  - **Critical:** Independent generators, no cross-dependencies

- **[performance-principles.md](performance-principles.md)** - Caching and optimization
  - **When to use:** Writing or optimizing generators
  - **Critical:** Syntactic filtering first, value type records for caching

### 🔨 Building Generators
- **[generator-patterns.md](generator-patterns.md)** - Three core patterns
  - **When to use:** Creating a new generator
  - **Critical:** Choose right pattern for your use case

- **[template-system.md](template-system.md)** - Template files and snippets
  - **When to use:** Generating C# code from templates
  - **Critical:** Templates are real C# files with IDE support

### 🔧 Development Practices
- **[value-type-records.md](value-type-records.md)** - Critical for caching
  - **When to use:** Creating info records for generators
  - **Critical:** MUST use sealed records, not classes

- **[diagnostic-system.md](diagnostic-system.md)** - Reporting diagnostics
  - **When to use:** Adding generator diagnostics
  - **Critical:** Use DiagnosticDescriptors, allocate IDs properly

### 📦 Project & Testing
- **[project-structure.md](project-structure.md)** - File organization, .csproj setup
  - **When to use:** Setting up generator project
  - **Critical:** Templates excluded from compilation, embedded as resources

- **[testing-strategy.md](testing-strategy.md)** - Unit, integration, snapshot tests
  - **When to use:** Writing generator tests
  - **Critical:** Test all three levels

### ⚠️ Avoiding Mistakes
- **[common-pitfalls.md](common-pitfalls.md)** - 7 major mistakes
  - **When to use:** Before writing generator code
  - **Critical:** Classes break caching, expensive predicates kill performance

- **[quick-reference.md](quick-reference.md)** - Checklists and examples
  - **When to use:** Quick lookup during development
  - **Critical:** Complete generator example, checklists

---

## 🎯 Common Scenarios

### "I'm creating a new generator"
1. Read: [architecture.md](architecture.md) - Understand the pattern
2. Read: [generator-patterns.md](generator-patterns.md) - Choose pattern
3. Read: [value-type-records.md](value-type-records.md) - Create info records
4. Read: [template-system.md](template-system.md) - If generating code
5. Read: [common-pitfalls.md](common-pitfalls.md) - Avoid mistakes
6. Reference: [quick-reference.md](quick-reference.md) - Checklist

### "Generator is slow"
1. Read: [performance-principles.md](performance-principles.md) - Optimization techniques
2. Check: Are you using sealed records? (value-type-records.md)
3. Check: Is predicate syntactic only? (performance-principles.md)
4. Check: Common-pitfalls.md - Expensive predicate, classes vs records

### "I need to generate code from templates"
1. Read: [template-system.md](template-system.md) - Template patterns
2. Read: [project-structure.md](project-structure.md) - .csproj configuration
3. Use: `TemplateUtilities.GetEmbeddedTemplate()`
4. Use: `TemplateUtilities.ReplaceRegion()`

### "I want to report diagnostics"
1. Read: [diagnostic-system.md](diagnostic-system.md) - Diagnostic patterns
2. Add descriptor to `DiagnosticDescriptors.cs`
3. Allocate ID (WHIZ0XX range)
4. Report with `context.ReportDiagnostic()`

### "Writing tests for generator"
1. Read: [testing-strategy.md](testing-strategy.md) - Three test levels
2. Unit tests: Test extraction logic
3. Integration tests: Verify generated code compiles
4. Snapshot tests: Verify output matches expected

### "I'm changing generator public APIs" (attributes, diagnostic IDs, etc.)
1. Read: [../../ai-docs/documentation-maintenance.md](../../ai-docs/documentation-maintenance.md) - Complete workflow
2. Ask: "What version are you working on?" (MANDATORY)
3. Add/update `<docs>` tag in source code
4. Update documentation in `whizbang-lib.github.io` repo
5. Regenerate code-docs mapping
6. Validate links with MCP tool
7. Commit both repos

---

## 🔑 Key Principles

### 1. Incremental Generators Only
- ✅ Use `IIncrementalGenerator` (not `ISourceGenerator`)
- ✅ Value-based caching with sealed records
- ✅ ~0ms on incremental builds when nothing changes

### 2. Performance First
- ✅ Syntactic filtering before semantic analysis (predicate)
- ✅ Early null returns in transform
- ✅ Static methods (no closures)
- ✅ Filter nulls: `.Where(static info => info is not null)`

### 3. Value Type Records (CRITICAL)
- ✅ MUST use `sealed record` for info types
- ✅ NEVER use classes (breaks caching)
- ✅ Use primary constructor
- ✅ Include XML documentation

### 4. Templates as Real C#
- ✅ Templates are real C# files with IDE support
- ✅ Placeholder types for IntelliSense
- ✅ Excluded from compilation, embedded as resources
- ✅ Region-based replacement

### 5. Independent Generators
- ✅ No cross-dependencies between generators
- ✅ Each generator has isolated cache
- ✅ Parallel execution
- ✅ Single responsibility

---

## 📖 Complete Documentation List

### Generator-Specific Documentation
1. **[README.md](README.md)** - This file (navigation hub)
2. **[architecture.md](architecture.md)** - Multiple Independent Generators
3. **[performance-principles.md](performance-principles.md)** - Caching and optimization
4. **[generator-patterns.md](generator-patterns.md)** - Three core patterns
5. **[template-system.md](template-system.md)** - Templates and snippets
6. **[value-type-records.md](value-type-records.md)** - Critical caching pattern
7. **[diagnostic-system.md](diagnostic-system.md)** - Reporting diagnostics
8. **[project-structure.md](project-structure.md)** - File organization, .csproj
9. **[testing-strategy.md](testing-strategy.md)** - Unit, integration, snapshot
10. **[common-pitfalls.md](common-pitfalls.md)** - 7 major mistakes
11. **[quick-reference.md](quick-reference.md)** - Checklists and examples

### Library-Level Documentation
- **[../../ai-docs/documentation-maintenance.md](../../ai-docs/documentation-maintenance.md)** - CRITICAL: Keep docs synchronized when changing generator public APIs

---

## 🚫 Common Mistakes (Quick List)

### ❌ NEVER Do These

1. **Use classes for info types**
   - Breaks incremental caching completely
   - Use `sealed record` instead

2. **Semantic analysis in predicate**
   - 100x slower compilation
   - Use syntactic checks only

3. **Forget to filter nulls**
   - NullReferenceException in generation
   - Use `.Where(static info => info is not null)`

4. **Use non-static methods**
   - Allocations, missed optimizations
   - Use `static` predicates and transforms

5. **Wrong template namespace**
   - "Template not found" error
   - Match actual namespace exactly

6. **Modify ImmutableArray**
   - Compilation error
   - Use `.ToBuilder()` pattern

7. **Forget CancellationToken**
   - Can't cancel generator, delays IDE
   - Pass `ct` to all semantic operations

8. **Change generator public APIs without updating documentation**
   - ALWAYS ask version first, update docs
   - See [../../ai-docs/documentation-maintenance.md](../../ai-docs/documentation-maintenance.md)

---

## 📊 Performance Impact

### Using Classes Instead of Records
- ❌ Class: 50-200ms every build (never cached)
- ✅ Record: 0ms incremental (cached)

### Bad Predicate (Semantic Analysis)
- ❌ Bad: ~5,000-10,000ms on 10,000 types
- ✅ Good: ~50-100ms on 10,000 types
- **Impact:** 100x slower!

### Predicate Rule
> Filter out 95%+ of nodes before semantic analysis

---

## 🎓 Learning Path

**New to source generators? Read in this order:**

1. **[architecture.md](architecture.md)** - Understand the pattern
2. **[performance-principles.md](performance-principles.md)** - Why things are done this way
3. **[value-type-records.md](value-type-records.md)** - Critical for caching
4. **[generator-patterns.md](generator-patterns.md)** - Choose your pattern
5. **[template-system.md](template-system.md)** - Generate code
6. **[common-pitfalls.md](common-pitfalls.md)** - Avoid mistakes
7. **[quick-reference.md](quick-reference.md)** - Checklist and examples

---

## 🔗 Related Documentation

### In This Repo
- `../CLAUDE.md` - Generators overview (to be simplified)
- `/ai-docs/` - Library-level AI documentation
- `/CLAUDE.md` - Library repo overview

### Other Repos
- `/Users/philcarbone/src/CLAUDE.md` - Workspace overview
- `/Users/philcarbone/src/whizbang/ai-docs/` - Core development docs

---

## ✅ Generator Development Checklist

Before claiming generator work complete:

- [ ] Uses `IIncrementalGenerator` interface
- [ ] Info types are `sealed record` (not classes)
- [ ] Predicate uses syntactic filtering only
- [ ] Transform has early null returns
- [ ] Nulls filtered: `.Where(static info => info is not null)`
- [ ] Methods are `static` where possible
- [ ] `CancellationToken` passed to semantic operations
- [ ] Diagnostics added to `DiagnosticDescriptors.cs`
- [ ] Templates properly configured in .csproj
- [ ] Unit tests written
- [ ] Integration tests verify compilation
- [ ] Snapshot tests check output
- [ ] Documentation updated if public APIs changed (attributes, diagnostic IDs, etc.)
- [ ] Code-docs mapping regenerated if `<docs>` tags added/changed
- [ ] Links validated with MCP tool

---

## 💡 Context Loading Strategy

**Don't load all docs!** Load contextually:

**For new generator:**
- Load: architecture.md, generator-patterns.md, value-type-records.md
- Skip: testing-strategy.md (until writing tests)

**For performance issue:**
- Load: performance-principles.md, common-pitfalls.md
- Skip: template-system.md, diagnostic-system.md

**For template work:**
- Load: template-system.md, project-structure.md
- Skip: performance-principles.md, testing-strategy.md

**For testing:**
- Load: testing-strategy.md, quick-reference.md
- Skip: architecture.md, performance-principles.md

---

**Remember:** These docs exist to help you work effectively on source generators. Use them as references, loading only what's relevant to your current task.
