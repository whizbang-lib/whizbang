# The Boy Scout Rule

**"Leave the codebase better than you found it."**

This is not a suggestion - it's a fundamental principle that applies to every code change, every session, every commit.

---

## Table of Contents

1. [Core Principle](#core-principle)
2. [Forbidden Excuses](#forbidden-excuses)
3. [Required Behaviors](#required-behaviors)
4. [Practical Examples](#practical-examples)
5. [Integration with TDD](#integration-with-tdd)
6. [Pre-Release Application](#pre-release-application)
7. [Cultural Impact](#cultural-impact)

---

## Core Principle

> **"Always leave the code better than you found it."**

This means:
- ✅ Fix issues you discover, even if they're "out of scope"
- ✅ Refactor while implementing features
- ✅ Improve tests even if not directly related
- ✅ Update documentation when code changes
- ✅ Consolidate duplicate code
- ✅ Remove dead code
- ✅ Fix root causes, not symptoms

---

## Forbidden Excuses

These excuses are **NEVER acceptable**:

### ❌ "That issue was pre-existing"

**WRONG:**
```
Developer: "I noticed these 4 duplicate coverage scripts, but my task
is just to add a new test. The duplicates were already there, so
I'm not touching them."
```

**Why this is wrong:**
- You noticed the problem
- You have the knowledge to fix it
- Ignoring it makes it worse (tech debt accumulates)
- Future developers will make the same excuse

**✅ CORRECT Response:**
```
Developer: "I noticed 4 duplicate coverage scripts. Let me:
1. Understand what each does
2. Consolidate into organized /scripts/coverage/ folder
3. Update documentation
4. THEN add my new test

Updating working plan to include script consolidation."
```

---

### ❌ "That's out of scope for this PR"

**WRONG:**
```
Developer: "I'm implementing feature X. I noticed the tests for
feature Y are fragile and poorly written, but that's not in the
scope of this PR, so I'll ignore it."
```

**✅ CORRECT:**
```
Developer: "While implementing feature X, I noticed tests for feature Y
are fragile. I'm going to:
1. Strengthen those tests first (preventing future issues)
2. Then implement feature X
3. Apply what I learned to write robust tests for X"
```

---

### ❌ "Someone else can fix that"

**WRONG:**
```
Developer: "I see inconsistent naming in this module, but I didn't
write it, so someone else should clean it up."
```

**Why this is wrong:**
- Diffusion of responsibility (everyone thinks someone else will do it)
- You're the person who noticed it NOW
- You have context loaded in your head RIGHT NOW

**✅ CORRECT:**
```
Developer: "I see inconsistent naming. Let me fix it while I'm
working in this area - I already have the context loaded."
```

---

### ❌ "It's not my responsibility"

**WRONG:**
```
Developer: "The documentation is outdated for this API, but I'm
a backend developer, not a documentation writer."
```

**✅ CORRECT:**
```
Developer: "I just changed this API. Let me update the documentation
immediately while the changes are fresh in my mind."
```

---

### ❌ "That's good enough for now"

**WRONG:**
```
Developer: "I got the tests passing with 60% branch coverage. The
goal is 100%, but 60% is good enough for now."
```

**✅ CORRECT:**
```
Developer: "I got the tests passing with 60% branch coverage. Our
goal is 100%. Let me identify the uncovered branches and add tests
to reach 100% before considering this complete."
```

---

## Required Behaviors

### 1. Fix Root Causes, Not Symptoms

**❌ Symptom Fix:**
```csharp
// Symptom: NullReferenceException
if (customer != null) {  // Band-aid
    ProcessOrder(customer);
}
```

**✅ Root Cause Fix:**
```csharp
// Root cause: Why is customer ever null?
// Fix: Ensure customer is always resolved or fail fast with meaningful error

var customer = await _repository.GetByIdAsync(customerId)
    ?? throw new CustomerNotFoundException(customerId);

ProcessOrder(customer);  // Guaranteed non-null
```

---

### 2. Update Working Plan When Issues Discovered

**Scenario:** You're implementing feature A, discover tech debt in area B.

**Process:**
1. ⏸️ Pause current work
2. 📝 Document what you discovered
3. 🔄 Update working plan/todo list
4. 🤔 Decide: fix now or later?
   - **Fix now** if: Small, prevents future issues, you have context
   - **Fix later** if: Large, requires design discussion, separate PR makes sense
5. ▶️ Proceed with clarity

**Example Todo Update:**
```markdown
## Current Work
- [ ] Implement OrderReceptor
- [x] Discovered: 4 duplicate coverage scripts
- [x] Decided: Fix now (small, improves codebase health)
- [ ] Consolidate coverage scripts → /scripts/coverage/
- [ ] Document scripts in scripts/README.md
- [ ] Resume: Implement OrderReceptor
```

---

### 3. Refactor While Implementing

The **REFACTOR** phase of TDD is when Boy Scout Rule shines:

**TDD Cycle:**
1. 🔴 RED: Write failing test
2. 🟢 GREEN: Make it pass (minimal code, can be messy)
3. 🔵 REFACTOR: **Apply Boy Scout Rule aggressively**
   - Clean up new code
   - Clean up existing code you touched
   - Improve tests
   - Update docs
   - Remove duplication
   - Fix naming inconsistencies

**Example:**
```csharp
// After GREEN phase - test passes but code is messy
public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
    // TODO: This works but needs cleanup
    var o = new Order();
    o.Id = Guid.CreateVersion7();
    o.CustomerId = message.CustomerId;
    o.TotalAmount = message.Items.Sum(i => i.Price * i.Quantity);
    await _repo.SaveAsync(o);
    return new OrderCreated(o.Id);
}

// REFACTOR phase - apply Boy Scout Rule
public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
    // Create domain entity with proper construction
    var order = Order.Create(
        id: OrderId.From(Guid.CreateVersion7()),
        customerId: message.CustomerId,
        items: message.Items);

    await _repository.SaveAsync(order);

    return new OrderCreated(order.Id);
}

// Also noticed: Order.Create method didn't validate items
// Added validation in Order.Create
// Added test for empty items validation
// Updated related documentation
```

---

### 4. Improve Tests Even If Not Directly Related

**Scenario:** You're fixing a bug, notice nearby tests are fragile.

**❌ WRONG:**
```
"I'm just fixing this bug. The test for CalculateTotal is fragile
and uses brittle mocking, but that's not my problem."
```

**✅ CORRECT:**
```
"While fixing this bug, I noticed CalculateTotal test is fragile.
Let me strengthen it:
- Replace brittle mock with proper Rocks expectations
- Add edge cases
- Use Bogus for test data generation
- Add descriptive assertion messages

This prevents future bugs in related code."
```

---

### 5. Update Documentation With Code Changes

**When you change code, documentation updates are NOT optional:**

```markdown
## Checklist for Code Changes

✅ Code updated
✅ Tests updated
✅ Tests passing
✅ Documentation updated  ← NOT OPTIONAL
✅ Examples updated (if API changed)
✅ Migration guide (if breaking change)
✅ Changelog entry
✅ Code formatted (dotnet format)
```

**Example:**
```
Changed: CreateOrder now requires ValidFrom date

Updated:
- API documentation in docs repo
- Code examples in all tutorials
- Migration guide for v2.0
- Tests to include ValidFrom
- XML doc comments on CreateOrder
```

---

### 6. Consolidate Duplicate Code

**Scenario:** You notice duplication while adding new code.

**❌ WRONG:**
```
"There are already 3 places that do similar validation. I'll
add a 4th because I don't want to refactor the existing code."
```

**✅ CORRECT:**
```
"I notice 3 places with similar validation. Let me:
1. Extract common validation logic
2. Create ValidateOrder helper
3. Update all 3 existing places to use it
4. Use it in my new code

Result: 4 call sites, 1 validation implementation (DRY principle)."
```

---

### 7. Remove Dead Code

**If you find dead code, delete it:**

```csharp
// ❌ WRONG - Commenting out dead code
// public class OldOrderProcessor {
//     // This was replaced by NewOrderProcessor
//     // Keeping for reference
// }

// ✅ CORRECT - Delete it
// (Git history preserves it if needed)
```

**Why delete instead of comment:**
- Git preserves history
- Comments clutter codebase
- Creates confusion ("Is this used?")
- Accumulates tech debt

---

## Practical Examples

### Example 1: Adding a New Test

**Task:** Add test for OrderReceptor

**Discovers:** 4 duplicate coverage scripts in root directory

**❌ BAD APPROACH:**
```
1. Add new test
2. Run tests
3. Notice duplicate scripts but ignore them
4. Commit
```

**✅ GOOD APPROACH (Boy Scout Rule):**
```
1. Notice 4 duplicate coverage scripts
2. Update working plan:
   - Add OrderReceptor test
   - Consolidate coverage scripts first
3. Create /scripts/coverage/ directory
4. Consolidate scripts:
   - collect-coverage.ps1
   - merge-coverage.ps1
   - show-coverage-report.ps1
   - run-all-tests-with-coverage.ps1
5. Update scripts/README.md
6. Delete duplicate .sh scripts
7. NOW add OrderReceptor test
8. Commit: "Consolidate coverage scripts + add OrderReceptor test"
```

**Result:** Codebase is better, new test added, tech debt reduced.

---

### Example 2: Fixing a Bug

**Task:** Fix NullReferenceException in OrderService

**Discovers:**
- Root cause is missing validation
- Related test is fragile (uses magic strings)
- Documentation doesn't mention validation requirement

**❌ BAD APPROACH:**
```
1. Add null check (symptom fix)
2. Test passes
3. Commit
```

**✅ GOOD APPROACH (Boy Scout Rule):**
```
1. Identify root cause: Missing validation in CreateOrder
2. Add proper validation with meaningful error
3. Fix fragile test:
   - Use Bogus for test data
   - Use Rocks for proper mocking
   - Add descriptive assertions
4. Add edge case tests (null, empty, invalid)
5. Update documentation with validation rules
6. Update code examples
7. Run dotnet format
8. Commit with comprehensive message
```

**Result:** Bug fixed, tests improved, docs updated, future bugs prevented.

---

### Example 3: Implementing New Feature

**Task:** Add causation tracking to messages

**Discovers:**
- Some tests don't use Async suffix
- MessageEnvelope needs XML doc comments
- Sample project would benefit from this feature

**✅ GOOD APPROACH (Boy Scout Rule):**
```
1. Implement causation tracking (TDD)
2. During REFACTOR phase:
   - Fix test naming (add Async suffixes)
   - Add XML doc comments to MessageEnvelope
   - Run dotnet format
3. Update sample project to use causation tracking
4. Update all documentation
5. Add migration guide
6. Commit staged work
```

**Result:** Feature added, naming consistency improved, docs complete.

---

## Integration with TDD

Boy Scout Rule **amplifies** TDD effectiveness:

### TDD Cycle with Boy Scout Rule

```
🔴 RED Phase:
- Write failing test
- Notice: Existing tests could be clearer
- UPDATE PLAN: Improve existing tests after GREEN

🟢 GREEN Phase:
- Write minimal code to pass test
- Notice: Similar code exists in another file
- UPDATE PLAN: Consolidate during REFACTOR

🔵 REFACTOR Phase: ⭐ BOY SCOUT RULE TIME ⭐
- Clean up new code
- Improve existing tests (from RED note)
- Consolidate duplication (from GREEN note)
- Update documentation
- Fix naming inconsistencies
- Add missing XML doc comments
- Remove dead code
- Run dotnet format
```

**Key:** REFACTOR phase is when you make things **better**, not just **working**.

---

## Pre-Release Application

Boy Scout Rule is **CRITICAL** before releases:

### Pre-Release Checklist (Boy Scout Enforcement)

```markdown
## Code Quality
- [ ] 100% branch coverage achieved (no gaps)
- [ ] All tests passing
- [ ] No flaky tests
- [ ] All code formatted (dotnet format)
- [ ] No TODO comments (fix or create issues)
- [ ] No dead code
- [ ] No duplicate code

## Documentation
- [ ] All API changes documented
- [ ] All examples updated
- [ ] Migration guide complete (if breaking)
- [ ] Changelog updated
- [ ] XML doc comments on all public APIs

## Technical Debt
- [ ] All known issues addressed (no "we'll get to it later")
- [ ] Scripts organized and consolidated
- [ ] Tests use modern patterns (TUnit, Rocks, Bogus)
- [ ] Naming conventions consistent
- [ ] No workarounds in sample projects

## Verification
- [ ] Native AOT publish succeeds
- [ ] All samples run successfully
- [ ] Integration tests pass
- [ ] Performance benchmarks acceptable
```

**NEVER ship with:**
- Known tech debt
- "Good enough" code
- Incomplete documentation
- Fragile tests
- Duplicate scripts
- Inconsistent patterns

---

## Cultural Impact

### What Boy Scout Rule Creates

**Positive Feedback Loop:**
```
Clean code → Easier to work with → More improvements → Cleaner code
```

**Team Benefits:**
- Everyone feels ownership
- Tech debt doesn't accumulate
- Code quality improves over time
- New developers see "this is how we do things"

**Personal Benefits:**
- Pride in craftsmanship
- Skills improve (constant refactoring practice)
- Easier to return to your own code
- Reputation for quality

---

### What Happens Without Boy Scout Rule

**Negative Spiral:**
```
Tech debt → "Too messy to fix" → More tech debt → "Rewrite needed"
```

**Team Problems:**
- "Someone else's problem" mentality
- Tech debt accumulates
- Code quality degrades
- New developers inherit mess

**Result:** Eventually, codebase becomes unmaintainable.

---

## Quick Reference

### Ask Yourself

Before committing, honestly answer:

- [ ] Is the code better than when I started?
- [ ] Did I fix issues I discovered?
- [ ] Is documentation up to date?
- [ ] Are tests strong and clear?
- [ ] Did I remove duplication?
- [ ] Did I fix naming inconsistencies?
- [ ] Would I be proud to show this code?

If **any** answer is NO, keep working.

---

### The 5-Minute Rule

**If improvement takes < 5 minutes, do it NOW:**

- Rename poorly named variable
- Add missing XML doc comment
- Fix inconsistent formatting
- Delete dead code
- Update outdated comment
- Add missing using statement

**If improvement takes > 5 minutes:**

1. Document it
2. Update working plan
3. Decide: Now or later?
4. If later: Create issue, don't forget

---

### Remember

> "I'm not just implementing feature X.
> I'm making the entire codebase better."

This mindset separates **good** developers from **great** developers.

---

## See Also

- [TDD Strict](tdd-strict.md) - RED/GREEN/REFACTOR cycle
- [Code Standards](code-standards.md) - Formatting and naming rules
- [Sample Projects](sample-projects.md) - No workarounds allowed
