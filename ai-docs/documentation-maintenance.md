# Documentation Maintenance

**CRITICAL**: When modifying public APIs in ANY library project (Whizbang.Core, Whizbang.Generators, Whizbang.Testing, etc.), you MUST update the corresponding documentation.

This document explains when and how to keep library code and documentation synchronized.

---

## Scope

This workflow applies to **ALL public APIs** in the whizbang repository:

- **Whizbang.Core** - Core interfaces, types, observability
- **Whizbang.Generators** - Source generator attributes and APIs
- **Whizbang.Testing** - Testing utilities
- **Whizbang.Transports.*** - Transport layer implementations
- **Any future library projects**

---

## Documentation Structure

Documentation lives in a single production version folder:

```
whizbang-lib.github.io/src/assets/docs/
├── v1.0.0/           # Production documentation (THE primary version)
├── drafts/           # Work in progress (not yet ready for release)
├── proposals/        # Feature proposals being considered
├── backlog/          # Future features not yet started
└── declined/         # Rejected proposals
```

**Key principle**: All pre-release versions (0.x.x alphas/betas) contribute to v1.0.0 documentation. There is no separate v0.1.0, v0.2.0, etc. documentation.

---

## `<docs>` Tag Standards

### ALWAYS Use Versionless Paths

```csharp
// CORRECT - versionless path
/// <docs>core-concepts/dispatcher</docs>

// WRONG - versioned path (never use these)
/// <docs>v1.0.0/core-concepts/dispatcher</docs>
```

**Why versionless?**
- Automatically resolves to current production version
- No code changes needed when documentation reorganizes
- Consistent across all library projects

### Tag Format

```csharp
/// <summary>
/// Dispatches messages to appropriate handlers
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
public interface IDispatcher {
  Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message);
}
```

---

## When Code Changes Require Documentation Updates

### Always Update Documentation When

- Adding new public interfaces, classes, or methods
- Changing method signatures (parameters, return types)
- Adding new attributes or changing their behavior
- Modifying behavior of existing APIs
- Adding or removing features
- Deprecating APIs
- Changing performance characteristics
- Updating error handling or exceptions thrown

### Documentation Updates NOT Required For

- Internal implementation changes (private methods, internal classes)
- Refactoring that doesn't change public API
- Performance optimizations with same behavior
- Bug fixes that restore documented behavior
- Code formatting or style changes

---

## Documentation Update Workflow

### Step 1: Update Library Code

Add or update the `<docs>` XML tag (versionless path):

```csharp
/// <summary>
/// Sends multiple messages in a single batch.
/// </summary>
/// <docs>core-concepts/dispatcher#send-many</docs>
public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(
  IEnumerable<TMessage> messages
) where TMessage : notnull;
```

### Step 2: Update Documentation Files

Navigate to documentation repository:

```bash
cd ../whizbang-lib.github.io
```

Edit the relevant documentation file:

```
src/assets/docs/v1.0.0/core-concepts/dispatcher.md
```

Include:
- Method/attribute signature
- Parameters with descriptions
- Return value description
- Code example demonstrating usage
- When to use this API

### Step 3: Regenerate Code-Docs Mapping

```bash
cd ../whizbang-lib.github.io
node src/scripts/generate-code-docs-map.mjs
```

This updates `src/assets/code-docs-map.json` with the latest mappings.

### Step 4: Validate Links

Ensure all `<docs>` tags point to valid documentation:

```bash
# Using MCP tool
mcp__whizbang-docs__validate-doc-links()

# Or using slash command
/verify-links
```

### Step 5: Rebuild Search Index

The search index rebuilds automatically during:
- `npm start` (development server)
- `npm run build` (production build)

Manual rebuild if needed:
```bash
./build-search-index.sh
```

### Step 6: Commit Both Repositories

```bash
# Library changes
cd ../whizbang
git add src/Whizbang.Core/IDispatcher.cs
git commit -m "feat(dispatcher): Add SendManyAsync method"

# Documentation changes
cd ../whizbang-lib.github.io
git add src/assets/docs/v1.0.0/core-concepts/dispatcher.md
git add src/assets/code-docs-map.json
git commit -m "docs(dispatcher): Document SendManyAsync method"
```

---

## Deprecation Guidelines

When deprecating an API:

### In Code

```csharp
[Obsolete("Use SendAsync<TMessage>() instead. Will be removed in v2.0.0")]
public Task<IDeliveryReceipt> Send(object message);
```

### In Documentation

```markdown
## Send (Deprecated)

:::deprecated
Deprecated in v1.0.0. Use `SendAsync<TMessage>()` instead.

**Migration**:
```csharp
// Before
await dispatcher.Send(command);

// After
await dispatcher.SendAsync(command);
```
:::
```

---

## Safety: Commit Before Deletions

**When deleting documentation sections, ALWAYS commit first:**

```bash
# 1. COMMIT CURRENT STATE
git add .
git commit -m "docs: State before deletion"

# 2. DELETE CONTENT
# (remove documentation section/file)

# 3. COMMIT DELETION
git add .
git commit -m "docs: Remove X feature documentation"
```

**Why**: Easy rollback if mistake with `git revert HEAD`

---

## Claude's Responsibility

**When Claude modifies public APIs, Claude MUST**:

1. Proactively offer to update documentation
2. Update documentation file in v1.0.0/
3. Use versionless `<docs>` paths in code
4. Regenerate the code-docs mapping
5. Validate links
6. Commit safely (before deletions)
7. Commit both repositories

**Claude MUST NOT**:

- Change public APIs without asking about documentation
- Use versioned paths in `<docs>` tags
- Delete documentation without committing first
- Skip link validation after changes

---

## Quick Checklist

When modifying public APIs, Claude should ask:

> I've modified the public API for `IDispatcher`. Would you like me to update the documentation?
>
> If yes, I'll:
> 1. Update documentation in `v1.0.0/core-concepts/dispatcher.md`
> 2. Regenerate the code-docs mapping
> 3. Validate all `<docs>` tag links
> 4. Rebuild the search index
> 5. Commit both repositories

If user agrees:
- [ ] Update documentation file
- [ ] Commit before deletion (if deleting content)
- [ ] Run `generate-code-docs-map.mjs`
- [ ] Validate with `mcp__whizbang-docs__validate-doc-links()`
- [ ] Commit documentation changes
- [ ] Commit library changes

---

## Tools and Commands

### MCP Tools

- `mcp__whizbang-docs__get-code-location({ concept: "dispatcher" })` - Find code implementing a doc
- `mcp__whizbang-docs__get-related-docs({ symbol: "IDispatcher" })` - Find docs for a symbol
- `mcp__whizbang-docs__validate-doc-links()` - Validate all `<docs>` tags

### Slash Commands

- `/verify-links` - Validate all `<docs>` tags
- `/rebuild-mcp` - Rebuild code-docs map and restart MCP server

### Scripts

```bash
# Regenerate mapping
node src/scripts/generate-code-docs-map.mjs

# Rebuild search index
./build-search-index.sh
```

---

## Summary

**Key Principles**:
1. **Single version** - All documentation lives in v1.0.0/
2. **Versionless `<docs>` paths** - Never include version in code tags
3. **Commit before deletions** - Safety net for mistakes
4. **Keep code and documentation synchronized**

**Claude's Role**:
- Proactively offer to update documentation when modifying public APIs
- Use versionless paths in all `<docs>` tags
- Follow the complete workflow if user agrees
