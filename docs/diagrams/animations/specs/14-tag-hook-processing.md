# Tag Hook Processing — Animation Spec

**Animation file:** `docs/diagrams/animations/14-tag-hook-processing.html`
**Steps:** 7
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** How `[MessageTag]` attributes on messages trigger cross-cutting concerns after successful handling. Shows the compile-time discovery model, DI-based hook resolution, priority-sorted execution chain, and payload mutation passing between hooks.

**Why it matters:** Tag hooks are the Whizbang mechanism for audit logging, real-time notifications, field encryption, metrics, and other cross-cutting operations. The priority ordering is critical — encryption must run after audit so audit captures plaintext.

**Intended audience:** Developers registering custom hooks; anyone needing to understand hook execution ordering; developers debugging why a hook fired before or after another.

**Conceptual prerequisite:** Understanding that messages are processed by receptors and that tags are attributes on the message class, discovered at compile time by source generators.

---

## 2. Visual Layout

Three-column grid (`grid-template-columns: 220px 1fr 220px`):

| Column | DOM IDs | Represents |
|--------|---------|------------|
| Left (220px) | `n-message`, `n-registry`, `n-stage`; tag badges `tag-audit`, `tag-signal`, `tag-encrypt` | Message + tag attributes + discovery infrastructure |
| Center (flex) | `hk-audit`, `hk-signal`, `hk-encrypt` (hook cards) | Priority-ordered hook execution chain |
| Right (220px) | `pl-original`, `pl-after-audit`, `pl-after-signal`, `pl-after-encrypt` (payload state cards) | Payload evolution after each hook |

**Hook card states** (`hk-audit`, `hk-signal`, `hk-encrypt`):
- Default: `opacity: 0.5`
- `.active`: `opacity: 1`, cyan border + glow
- `.done`: `opacity: 1`, green border, `var(--phase-perspective-bg)` background
- `.skipped`: `opacity: 0.3` (not used in this animation)

**Payload state card visibility**: hidden (`opacity: 0`) until `showPL()` applies `.visible`. `.mutated` applies `var(--phase-cascade-bg)` background + gold border to indicate payload was changed.

**Tag badge states**: `.active` applies `border-color: var(--wb-cyan)` + dispatch-blue background. Each badge also has a type-specific color class (`audit`, `signal`, `encrypt`).

**Reset:** `resetAll()` — removes `.glow`/`.active`/`highlight-success` from nodes; removes `.active`/`.done`/`.skipped` from hook cards; removes `.active` from tag badges; removes `.visible`/`.mutated` from payload cards.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| `IMessageTagHook<TAttribute>` | `src/Whizbang.Core/Tags/IMessageTagHook.cs` | Interface method `OnTaggedMessageAsync(TagContext<TAttribute>, CancellationToken)` returns `ValueTask<JsonElement?>`; null = pass-through |
| `MessageTagAttribute` | `src/Whizbang.Core/Attributes/MessageTagAttribute.cs` | Base class for all tag attributes |
| `IMessageTagRegistry` | `src/Whizbang.Core/Tags/IMessageTagRegistry.cs` | Method `GetTagsFor(Type messageType)` returns `IEnumerable<MessageTagRegistration>` |
| `MessageTagRegistration` | `src/Whizbang.Core/Tags/MessageTagRegistration.cs` | Contains the tag attribute instance + hook type + priority |
| `TagContext<TAttribute>` | `src/Whizbang.Core/Tags/TagContext.cs` | Properties: `Message`, `Attribute`, `Payload` (merged payload from prior hooks) |
| `LifecycleStage.AfterReceptorCompletion` | `src/Whizbang.Core/Messaging/LifecycleStage.cs` | Value = `-1`; this is the stage at which tag hooks fire — step 1 narration references it |
| Hook registration pattern | `src/Whizbang.Core/` (DI registration) | `options.Tags.UseHook<TAttribute, THook>(priority)` — if registration API changes, steps 1 and 3 narrations need updating |

---

## 4. Steps Specification

### `message` — Message Handled Successfully (2500ms)

**Narration:** `OrderPlacedEvent` has been successfully processed by its receptor. Tag hook processing begins at `LifecycleStage.AfterReceptorCompletion` (-1). Hooks only fire after successful handling.

**DOM on enter:** `n-message` gets `.glow`; `n-stage` gets `.glow`
**DOM on exit:** `resetAll()`

**Source symbols:** `LifecycleStage.AfterReceptorCompletion`

**Intent:** Establishes the precondition — hooks only fire on success. AfterReceptorCompletion is the stage value (-1) that positions hooks before the normal stage 0 (ImmediateDetached).

---

### `discover` — Discover Tags (2500ms)

**Narration:** `IMessageTagRegistry.GetTagsFor(typeof(OrderPlacedEvent))` returns three tag registrations: `[Audit]`, `[SignalR]`, `[Encrypt]`. These were discovered at compile time by the source generator.

**DOM on enter:** `n-registry` gets `.glow`; `tag-audit`, `tag-signal`, `tag-encrypt` all get `.active`
**DOM on exit:** `resetAll()`

**Source symbols:** `IMessageTagRegistry.GetTagsFor()`, `MessageTagRegistration` — compile-time discovery

**Intent:** Shows that tag discovery is zero-reflection — the source generator created the registry at compile time.

---

### `resolve` — Resolve & Sort Hooks (2500ms)

**Narration:** Hooks resolved from DI container and sorted by priority (ascending): AuditLogHook (-10) first, SignalRNotificationHook (0) second, FieldEncryptionHook (100) last. Lower priority number = executes first.

**DOM on enter:** `hk-audit`, `hk-signal`, `hk-encrypt` all `opacity: 1`; `pl-original` gets `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `MessageTagRegistration` — priority field; DI hook resolution

**Intent:** Establishes the ordering rule. The original payload is shown to set a baseline for the mutation steps that follow.

---

### `hook-audit` — Hook 1: AuditLogHook (3000ms)

**Narration:** `AuditLogHook.OnTaggedMessageAsync(context)` — logs to audit trail. Returns modified payload with `auditId` added. This mutated payload is passed to the next hook.

**DOM on enter:** `hk-audit` gets `.active`; `tag-audit` gets `.active`; `pl-original` `.visible`; `pl-after-audit` `.visible` + `.mutated`
**DOM on exit:** `resetAll()`

**Source symbols:** `IMessageTagHook<TAttribute>.OnTaggedMessageAsync()` — non-null return mutates the payload chain; `TagContext<TAttribute>.Payload`

**Intent:** Shows hook execution and payload mutation. The audit hook adds a field before passing to subsequent hooks.

---

### `hook-signal` — Hook 2: SignalRNotificationHook (3000ms)

**Narration:** `SignalRNotificationHook.OnTaggedMessageAsync(context)` — pushes real-time notification to SignalR group. Returns `null` (no payload mutation). The previous payload (with auditId) passes through unchanged.

**DOM on enter:** `hk-audit` gets `.done`; `hk-signal` gets `.active`; `tag-signal` gets `.active`; `pl-original`, `pl-after-audit`, `pl-after-signal` all `.visible`
**DOM on exit:** `resetAll()`

**Source symbols:** `IMessageTagHook<TAttribute>.OnTaggedMessageAsync()` — null return = pass-through

**Intent:** Contrasts with step 4 — shows that hooks can have side effects without mutating the payload.

---

### `hook-encrypt` — Hook 3: FieldEncryptionHook (3000ms)

**Narration:** `FieldEncryptionHook.OnTaggedMessageAsync(context)` — encrypts sensitive fields (cardNumber). Returns mutated payload with encrypted values. This is why encryption runs LAST (priority: 100) — after audit logging captured the original.

**DOM on enter:** `hk-audit`, `hk-signal` get `.done`; `hk-encrypt` gets `.active`; `tag-encrypt` gets `.active`; all four payload cards `.visible`; `pl-after-encrypt` gets `.mutated`
**DOM on exit:** `resetAll()`

**Source symbols:** `IMessageTagHook<TAttribute>.OnTaggedMessageAsync()` — return value replaces the payload for subsequent hooks

**Intent:** Demonstrates priority ordering consequence — encryption at priority 100 runs last, ensuring audit at priority -10 captured plaintext.

---

### `complete` — Chain Complete (2500ms)

**Narration:** All 3 hooks executed in priority order. Final payload has: audit metadata (from hook 1), SignalR notification sent (hook 2, no mutation), sensitive fields encrypted (hook 3). Priority ordering ensured audit saw plaintext before encryption.

**DOM on enter:** `hk-audit`, `hk-signal`, `hk-encrypt` all `.done`; all four payload cards `.visible`; `pl-after-encrypt` `.mutated`
**DOM on exit:** `resetAll()`

**Source symbols:** none (summary step)

**Intent:** Reinforces the full chain and the critical ordering insight.

---

## 5. Maintenance Guide

**`IMessageTagHook<TAttribute>` interface changes** (`src/Whizbang.Core/Tags/IMessageTagHook.cs`):
- If `OnTaggedMessageAsync` return type changes from `ValueTask<JsonElement?>` → update steps 4 and 6 narrations (null = pass-through semantics)
- If method is renamed → update all step narrations that call it

**`TagContext<TAttribute>` changes** (`src/Whizbang.Core/Tags/TagContext.cs`):
- If `Payload` property renamed → step 3 and resolve context
- If new properties added → consider whether steps 4–6 should mention them

**`IMessageTagRegistry.GetTagsFor()` changes** (`src/Whizbang.Core/Tags/IMessageTagRegistry.cs`):
- If signature changes → step 2 narration

**`LifecycleStage.AfterReceptorCompletion` value changes** (`src/Whizbang.Core/Messaging/LifecycleStage.cs`):
- If value changes from -1 → update step 1 narration (currently says "-1")
- If stage is removed or renamed → step 1 narration

**Hook registration API changes**:
- If `options.Tags.UseHook<TAttribute, THook>(priority)` signature changes → update step 3 narration (mentions priority numbers -10, 0, 100)
- If default priority changes from -100 → no animation impact (hooks in animation are explicitly prioritized)

**What does NOT require an update:**
- Changes to `MessageHop`, `OutboxRecord`, `InboxRecord`, or transport-layer code
- Changes to the example hook implementations (`AuditLogHook`, `SignalRNotificationHook`, `FieldEncryptionHook`) — these are illustrative user-space implementations, not Whizbang framework code
- Changes to `IDispatcher` or `LifecycleCoordinator` (different stage of processing)
