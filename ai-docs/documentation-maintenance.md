# Documentation Maintenance

**CRITICAL**: When modifying public APIs in ANY library project (Whizbang.Core, Whizbang.Generators, Whizbang.Testing, etc.), you MUST update the corresponding documentation in the documentation repository.

This document explains when and how to keep library code and documentation synchronized across all projects.

---

## Scope

This workflow applies to **ALL public APIs across ALL projects** in the whizbang repository:

- **Whizbang.Core** - Core interfaces, types, observability (IDispatcher, IReceptor, etc.)
- **Whizbang.Generators** - Source generator attributes, APIs (GenerateDispatcherAttribute, etc.)
- **Whizbang.Testing** - Testing utilities (future)
- **Any future library projects**

Whether you're modifying a core interface or adding a generator attribute, the same documentation maintenance workflow applies.

---

## Version Awareness - CRITICAL

**Before making ANY documentation changes, Claude MUST determine the version being worked on.**

### Ask the User First

Claude should ask:
> What version are you working on? (e.g., v0.1.0, v0.2.0, v1.0.0)

### Version Determines Documentation Strategy

**Working on SAME version as existing documentation** (e.g., docs show v0.1.0, working on v0.1.0):
- ✅ **Update in place** - Modify existing documentation files
- ✅ **Delete features** - Remove documentation for features being removed
- ✅ **No deprecation** - Don't mark new features as deprecated!
- ⚠️ **Commit before deletion** - Safety net in case of mistakes

**Working on NEXT version** (e.g., docs show v0.1.0, working on v0.2.0):
- ✅ **Deprecate old** - Mark old APIs with deprecation callouts
- ✅ **Add new** - Document new APIs alongside old
- ✅ **Create v0.2.0 folder** - Or use drafts/ folder for unreleased
- ❌ **Don't delete old** - Keep for backward compatibility

### Common Mistakes Without Version Awareness

**Mistake 1: Marking brand new feature as deprecated**
```markdown
❌ WRONG (same version):
:::deprecated
Use NewMethod instead. (But NewMethod didn't exist before!)
:::

✅ CORRECT (same version):
## NewMethod
Use this method for... (Just document it, no deprecation!)
```

**Mistake 2: Deleting documentation that should be deprecated**
```markdown
❌ WRONG (next version):
Delete old method documentation entirely

✅ CORRECT (next version):
:::deprecated
Deprecated as of v0.2.0. Use NewMethod instead.
:::
```

**Mistake 3: Updating in place when versioning needed**
```markdown
❌ WRONG (next version):
Replace v0.1.0/dispatcher.md content

✅ CORRECT (next version):
Create v0.2.0/dispatcher.md or drafts/dispatcher.md
Keep v0.1.0/dispatcher.md for existing users
```

---

## When Code Changes Require Documentation Updates

### Always Update Documentation When

- ✅ Adding new public interfaces, classes, or methods (any project)
- ✅ Changing method signatures (parameters, return types)
- ✅ Adding new generator attributes or changing their behavior
- ✅ Modifying behavior of existing APIs
- ✅ Adding or removing features
- ✅ Deprecating APIs (next version only!)
- ✅ Changing performance characteristics
- ✅ Updating error handling or exceptions thrown

### Documentation Updates NOT Required For

- ❌ Internal implementation changes (private methods, internal classes)
- ❌ Refactoring that doesn't change public API
- ❌ Performance optimizations with same behavior
- ❌ Bug fixes that restore documented behavior
- ❌ Code formatting or style changes

---

## Documentation Update Workflow

### Step 0: Determine Version (MANDATORY)

**Ask user**:
> What version are you working on?

**Determine strategy**:
- Same version → Update in place
- Next version → Create new version folder or use drafts/

### Step 1: Update Library Code

Add or update the `<docs>` XML tag:

**Example: Core Interface**
```csharp
/// <summary>
/// Dispatches messages to appropriate handlers
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
public interface IDispatcher {
  Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message);
}
```

**Example: Generator Attribute**
```csharp
/// <summary>
/// Generates dispatcher implementation for the decorated class
/// </summary>
/// <docs>source-generators/receptor-discovery</docs>
[AttributeUsage(AttributeTargets.Class)]
public class GenerateDispatcherAttribute : Attribute {
  public string? Name { get; set; }
}
```

### Step 2: Update Documentation Files

Navigate to documentation repository:

```bash
cd ../whizbang-lib.github.io
```

**Determine file path based on version**:

**Same version** (e.g., working on v0.1.0):
```
src/assets/docs/v0.1.0/core-concepts/dispatcher.md  (update in place)
src/assets/docs/v0.1.0/source-generators/receptor-discovery.md  (update in place)
```

**Next version** (e.g., working on v0.2.0):
```
src/assets/docs/drafts/core-concepts/dispatcher.md  (create draft)
OR
src/assets/docs/v0.2.0/core-concepts/dispatcher.md  (create new version)
```

Update documentation to include:

**New APIs**:
- Method/attribute signature
- Parameters/properties with descriptions
- Return value description (if applicable)
- Code example demonstrating usage
- When to use this API

**Changed APIs**:
- Updated signature
- What changed and why
- Migration guide (if breaking and next version)
- Updated code examples

**Deprecated APIs** (next version only):
- Add deprecation callout
- Recommend replacement API
- Migration instructions

Example deprecation callout (next version):
```markdown
:::deprecated
This method is deprecated as of v0.2.0. Use `NewMethod()` instead.
See [Migration Guide](#migration) for details.
:::
```

**Removed APIs** (same version):
- **COMMIT FIRST** (safety net!)
- Delete documentation section
- Remove from examples
- **COMMIT AGAIN** (so deletion can be reverted if mistake)

### Step 3: Regenerate Code-Docs Mapping

After updating documentation, regenerate the mapping file:

```bash
# From documentation repository
cd ../whizbang-lib.github.io
node src/scripts/generate-code-docs-map.mjs
```

This updates `src/assets/code-docs-map.json` with the latest symbol-to-docs mappings.

### Step 4: Validate Links

Ensure all `<docs>` tags point to valid documentation:

**Option 1: Use MCP tool** (if Claude Code running):
```typescript
mcp__whizbang-docs__validate-doc-links()
```

**Option 2: Use slash command**:
```bash
/verify-links
```

Expected output:
```json
{
  "valid": 5,
  "broken": 0,
  "details": [...]
}
```

If broken links found:
- Check if documentation file exists at the specified path
- Verify the path format matches `category/doc-name`
- Update the `<docs>` tag or create missing documentation
- Regenerate mapping with step 3
- Re-validate

### Step 5: Rebuild Search Index

The search index rebuilds automatically during:
- `npm start` (development server)
- `npm run build` (production build)

Manual rebuild (if needed):
```bash
cd ../whizbang-lib.github.io
./build-search-index.sh
```

### Step 6: Commit Both Repositories

Commit library and documentation changes together:

```bash
# Commit library changes
cd ../whizbang
git add src/Whizbang.Core/IDispatcher.cs  # or Generators, Testing, etc.
git commit -m "feat(dispatcher): Add new SendManyAsync method"

# Commit documentation changes
cd ../whizbang-lib.github.io
git add src/assets/docs/v0.1.0/core-concepts/dispatcher.md
git add src/assets/code-docs-map.json
git add src/assets/search-index.json
git add src/assets/enhanced-search-index.json
git commit -m "docs(dispatcher): Document SendManyAsync method"
```

---

## Version-Specific Scenarios

### Scenario 1: Adding Feature to Current Release (Same Version)

**Context**: Working on v0.1.0, documentation already shows v0.1.0

**Action**: Update in place, no deprecation

**Example: Adding Core Interface Method**
```csharp
// New method in v0.1.0
public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(
  IEnumerable<TMessage> messages
) where TMessage : notnull;
```

**Documentation**:
```markdown
<!-- Update: src/assets/docs/v0.1.0/core-concepts/dispatcher.md -->

## SendManyAsync

Send multiple messages in a single batch.

**Signature**:
```csharp
Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(
  IEnumerable<TMessage> messages
) where TMessage : notnull;
```

**Example**:
```csharp
var messages = new[] { command1, command2, command3 };
var receipts = await dispatcher.SendManyAsync(messages);
```
```

**No deprecation callout** - this is a new feature!

**Example: Adding Generator Attribute Property**
```csharp
// New property in v0.1.0
[AttributeUsage(AttributeTargets.Class)]
public class GenerateDispatcherAttribute : Attribute {
  public string? Name { get; set; }
  public bool IncludeMetrics { get; set; }  // NEW
}
```

**Documentation**:
```markdown
<!-- Update: src/assets/docs/v0.1.0/source-generators/receptor-discovery.md -->

## GenerateDispatcher Attribute

### Properties

**IncludeMetrics** (optional)
- Type: `bool`
- Default: `false`
- Generates performance metrics collection code

**Example**:
```csharp
[GenerateDispatcher(IncludeMetrics = true)]
public partial class MyDispatcher { }
```
```

### Scenario 2: Removing Feature from Current Release (Same Version)

**Context**: Working on v0.1.0, removing a feature that was never released

**Action**: Delete documentation, commit before and after

```bash
# STEP 1: Commit current state (safety net)
git add .
git commit -m "docs: State before removing unreleased feature"

# STEP 2: Delete documentation
# Remove section from dispatcher.md or receptor-discovery.md

# STEP 3: Commit deletion
git add .
git commit -m "docs: Remove documentation for unreleased SendLegacy method"
```

**If mistake**: `git revert HEAD` restores the deleted documentation

### Scenario 3: Deprecating Feature in Next Release (Next Version)

**Context**: Working on v0.2.0, documentation shows v0.1.0

**Action**: Create v0.2.0 docs or use drafts/, mark old as deprecated

**Example: Core Interface**
```csharp
// In v0.2.0
[Obsolete("Use SendAsync<TMessage> instead. Will be removed in v1.0.0")]
public Task<IDeliveryReceipt> Send(object message);

public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message);
```

**Documentation** (in drafts/ or v0.2.0/):
```markdown
## SendAsync (Recommended)

Type-safe message sending with AOT compatibility.

## Send (Deprecated)

:::deprecated
Deprecated as of v0.2.0. Use `SendAsync<TMessage>()` instead.

**Migration**:
```csharp
// Before
await dispatcher.Send(command);

// After
await dispatcher.SendAsync(command);
```

Will be removed in v1.0.0.
:::
```

**Keep v0.1.0 docs unchanged** - existing users still need them!

**Example: Generator Attribute**
```csharp
// In v0.2.0
[Obsolete("Use GenerateDispatcherAttribute instead")]
public class GenerateHandlerAttribute : Attribute { }

public class GenerateDispatcherAttribute : Attribute { }
```

**Documentation** (in drafts/ or v0.2.0/):
```markdown
## GenerateDispatcher (Recommended)

Use this attribute to generate dispatcher implementations.

## GenerateHandler (Deprecated)

:::deprecated
Deprecated as of v0.2.0. Use `[GenerateDispatcher]` instead.

**Migration**:
```csharp
// Before
[GenerateHandler]
public partial class MyHandler { }

// After
[GenerateDispatcher]
public partial class MyDispatcher { }
```

Will be removed in v1.0.0.
:::
```

### Scenario 4: Breaking Change in Next Release (Next Version)

**Context**: Working on v1.0.0, changing method signature

**v0.x.x**:
```csharp
Task<IDeliveryReceipt> SendAsync(object message);
```

**v1.0.0**:
```csharp
Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull;
```

**Documentation** (in drafts/ or v1.0.0/):
```markdown
## SendAsync

:::new{type="breaking"}
**Breaking Change in v1.0.0**: Now requires generic type parameter for AOT compatibility.
:::

**Signature**:
```csharp
Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull;
```

**Migration from v0.x**:
```csharp
// v0.x (no longer supported)
await dispatcher.SendAsync(command);

// v1.0.0 (type parameter required)
await dispatcher.SendAsync<CreateOrder>(command);
```
```

---

## Safety: Commit Before Deletions

**When deleting documentation sections or files, ALWAYS commit first:**

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

**Why**:
- Easy rollback if mistake: `git revert HEAD`
- Clear audit trail of what was deleted
- Protects against accidental deletions

**When NOT to commit before deletion**:
- Deleting typos or minor corrections
- Removing duplicate content
- Fixing broken links

---

## Claude's Responsibility

**When Claude modifies public APIs in ANY project, Claude MUST**:

1. ✅ **Ask for version** - "What version are you working on?"
2. ✅ **Determine strategy** - Same version vs. next version
3. ✅ **Proactively offer** - Ask if documentation should be updated
4. ✅ **Update documentation** - If user agrees, follow correct strategy
5. ✅ **Regenerate mapping** - After documentation changes
6. ✅ **Validate links** - Ensure no broken references
7. ✅ **Commit safely** - Commit before deletions, commit both repos

**Claude MUST NOT**:

- ❌ Change public APIs without asking about documentation
- ❌ Make documentation changes without asking version
- ❌ Mark new features as deprecated in same version
- ❌ Delete old docs when working on next version
- ❌ Update in place when versioning needed
- ❌ Delete documentation without committing first
- ❌ Skip link validation after changes

---

## Quick Decision Tree

```
Public API changed? (Core, Generators, Testing, etc.)
├─ Yes → Ask: "What version are you working on?"
│   ├─ Same version as docs
│   │   ├─ Adding feature? → Update in place, no deprecation
│   │   ├─ Removing feature? → Commit, delete, commit
│   │   └─ Changing feature? → Update in place
│   └─ Next version
│       ├─ Adding feature? → Create new version docs or drafts/
│       ├─ Removing feature? → Mark as deprecated in new version
│       └─ Changing feature? → Document both, mark old as deprecated
└─ No → No documentation update needed
```

---

## Claude's Checklist

When modifying public APIs (in any project), Claude should ask:

> I've modified the public API for `IDispatcher` (or `GenerateDispatcherAttribute`, etc.). Before updating documentation:
>
> **What version are you working on?** (e.g., v0.1.0, v0.2.0)
>
> Then I'll:
> 1. Update documentation using the correct strategy (in-place vs. versioned)
> 2. Regenerate the code-docs mapping
> 3. Validate all `<docs>` tag links
> 4. Rebuild the search index
> 5. Commit before any deletions (if applicable)
> 6. Commit both repositories

If user says yes:
- [ ] Confirm version being worked on
- [ ] Update documentation file (correct strategy)
- [ ] Commit before deletion (if deleting content)
- [ ] Run `generate-code-docs-map.mjs`
- [ ] Validate with `mcp__whizbang-docs__validate-doc-links()`
- [ ] Rebuild search index (automatic or manual)
- [ ] Commit documentation changes
- [ ] Commit library changes

---

## Tools and Commands

### MCP Tools

- `mcp__whizbang-docs__get-code-location({ concept: "dispatcher" })` - Find code implementing a doc
- `mcp__whizbang-docs__get-related-docs({ symbol: "IDispatcher" })` - Find docs for a symbol
- `mcp__whizbang-docs__validate-doc-links()` - Validate all `<docs>` tags

### Slash Commands

- `/verify-links` - Validate all `<docs>` tags point to valid documentation
- `/rebuild-mcp` - Rebuild code-docs map and restart MCP server

### Scripts

```bash
# Regenerate mapping
cd ../whizbang-lib.github.io
node src/scripts/generate-code-docs-map.mjs

# Rebuild search index
./build-search-index.sh
```

---

## Summary

**Key Principles**:
1. **Always ask version first** - Different versions require different strategies
2. **Applies to ALL projects** - Core, Generators, Testing, and future projects
3. **Same version = update in place** - No deprecation for new features
4. **Next version = versioned docs** - Deprecate old, document new
5. **Commit before deletions** - Safety net for mistakes
6. **Library code and documentation must stay synchronized**

**Claude's Role**:
- Ask version before any documentation changes
- Follow version-specific strategy
- Proactively offer to update documentation when modifying public APIs in ANY project
- Commit before deletions for safety
- Complete the full workflow if user agrees
