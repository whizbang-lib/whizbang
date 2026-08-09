# Source Generator Pipeline — Animation Spec

**Animation file:** `docs/diagrams/animations/13-source-generator-pipeline.html`
**Steps:** 6
**Last verified against source:** 2026-04-05

---

## 1. Overview

**What this teaches:** The compile-time pipeline that enables Whizbang's zero-reflection, AOT-compatible design. Shows how Roslyn source generators use a two-pass funnel — cheap syntactic filtering then expensive semantic analysis — to efficiently discover receptors, perspectives, and message types from 10,000+ compilation types in ~275ms.

**Why it matters:** The "zero reflection at runtime" property depends entirely on this pipeline generating correct compile-time artifacts. Developers extending the framework with new generators, or debugging why a receptor isn't being discovered, need to understand this funnel. The 100x performance difference between good and bad predicates is a real build-time cost.

**Intended audience:** Framework extenders writing new source generators; developers debugging receptor/perspective discovery failures; anyone asking "why isn't my type being picked up?"

**Conceptual prerequisite:** Basic understanding that source generators run during compilation (not at runtime) and produce C# source files that are compiled alongside user code.

---

## 2. Visual Layout

Vertical flex layout:

| Region | DOM IDs | Represents |
|--------|---------|------------|
| Funnel strip | `fs-input`, `fs-syntactic`, `fs-semantic`, `fs-record`, `fs-output` + `fc-input`–`fc-output` count spans | 5-stage funnel visualization with item counts |
| Arrow separators | `.arrow-between` elements | Visual flow between stages |
| Detail row | `dc-left`, `dc-right` (title, code, note spans) | Two-panel code/explanation cards |

**Funnel stage states** (`fs-*`):
- Default: `opacity: 0.5`, neutral border
- `.active`: `opacity: 1`, cyan border + glow — current stage
- `.done`: `opacity: 1`, green border, `var(--phase-perspective-bg)` — completed
- `.filtered`: `opacity: 0.3`, dashed border — items filtered out at this stage

**Detail card visibility** (`dc-left`, `dc-right`): hidden (`opacity: 0`) until `showDet()` applies `.visible`.

**Cost badge** (`.fs-cost.cheap` / `.fs-cost.expensive`): visual indicator inside each stage card.

**Reset:** `resetAll()` — removes `.active`/`.done`/`.filtered` from funnel stages; hides detail cards; resets count spans to `—`.

---

## 3. Source References

| Symbol | Source File | What to Check |
|--------|-------------|---------------|
| Source generator infrastructure | `src/Whizbang.Generators/` | All generator files; check `ai-docs/performance-principles.md` for updated guidelines |
| `ClassDeclarationSyntax` check | `src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs` | Syntactic predicate implementation — step 2 |
| `GetDeclaredSymbol()` / semantic analysis | `src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs` | Semantic analysis implementation — step 3 |
| `sealed record` pattern for caching | `src/Whizbang.Generators/ai-docs/value-type-records.md` | Why sealed records are required for incremental caching — step 4 |
| `#region` template system | `src/Whizbang.Generators/Templates/` | Template files with region markers — step 5 |
| `context.AddSource()` | `src/Whizbang.Generators/` | How generated files are registered — step 5 |
| Generated output files | `src/Whizbang.Core/` (generated) | `ReceptorRegistry.g.cs`, `MessageTypeRegistry.g.cs`, etc. — step 6 |
| Performance guidelines | `src/Whizbang.Generators/ai-docs/performance-principles.md` | Updated benchmarks; step 2 says ~50ms and ~5000ms |

---

## 4. Steps Specification

### `input` — Compilation Input (2500ms)

**Narration:** Roslyn compilation contains ~10,000 type declarations. The source generator must find the handful that are Whizbang receptors, perspectives, or message types. Scanning all semantically would take 5-10 seconds.

**DOM on enter:** `fs-input` gets `.active`; `fc-input` = "10,000"
**DOM on exit:** `resetAll()`

**Source symbols:** none — establishes the scale problem

**Intent:** Sets up the problem: 10,000 types, need ~25 matches, semantic scanning everything is too slow.

---

### `syntactic` — Syntactic Predicate (Cheap) (3500ms)

**Narration:** First pass: syntactic filtering only. Check if node is a `ClassDeclarationSyntax` with base types. NO semantic model access — just syntax tree walking. Filters out 95%+ of nodes in ~50ms. This is the critical performance optimization.

**DOM on enter:** `fs-input` `.done`; `fs-syntactic` `.active`; `fc-syntactic` = "~500"; detail cards `.visible` with code/performance comparison
**DOM on exit:** `resetAll()`

**Source symbols:** `ClassDeclarationSyntax`, Roslyn syntactic predicate pattern

**Intent:** Shows the cheap first pass. The detail cards display the actual predicate code and the 100x performance comparison.

---

### `semantic` — Semantic Analysis (Expensive) (3500ms)

**Narration:** Second pass: semantic analysis on the ~500 surviving candidates. `GetDeclaredSymbol()`, check attributes (`[Receptor]`), verify interface implementations (`IReceptor<T>`). Expensive but only on 5% of nodes.

**DOM on enter:** `fs-input`, `fs-syntactic` `.done`; `fs-semantic` `.active`; `fc-syntactic` = "~500"; `fc-semantic` = "~25"; detail cards show transform method code and what semantic analysis checks
**DOM on exit:** `resetAll()`

**Source symbols:** `GetDeclaredSymbol()`, `IReceptor<T>` interface check, `[Receptor]` attribute

**Intent:** Shows why the 2-pass approach is essential — semantic analysis is only applied to the 5% that passed syntactic filtering.

---

### `record` — Value-Type Record (Caching) (3000ms)

**Narration:** Results stored as `sealed record` with value equality. On recompilation, if the record equals the previous run's record, code generation is SKIPPED entirely (incremental caching). This is why `sealed record` is mandatory.

**DOM on enter:** `fs-input`–`fs-semantic` `.done`; `fs-record` `.active`; `fc-semantic` = "~25"; `fc-record` = "~25"; detail cards show record definition and why-matters comparison
**DOM on exit:** `resetAll()`

**Source symbols:** `sealed record` (C# feature); Roslyn incremental caching via structural equality

**Intent:** Shows why the caching mechanism works. The contrast between `sealed record` (structural equality = cache works) and `class` (reference equality = always regenerates) is the key insight.

---

### `template` — Template Code Generation (3000ms)

**Narration:** Templates loaded with `#region` markers. Generator replaces regions with generated content: receptor registration, dispatch delegates, type mappings. Output registered via `context.AddSource()`.

**DOM on enter:** `fs-input`–`fs-record` `.done`; `fs-output` `.active`; all counts shown; detail cards show template syntax and `AddSource` calls
**DOM on exit:** `resetAll()`

**Source symbols:** `#region` template system in `src/Whizbang.Generators/Templates/`; `context.AddSource()`

**Intent:** Shows how the discovered type information becomes actual C# source code.

---

### `summary` — Pipeline Summary (3000ms)

**Narration:** 10,000 types → 500 (syntactic) → 25 (semantic) → 25 records → 25 source files. Total: ~275ms. Without syntactic filtering: ~5,000ms. The funnel pattern is the key to fast builds with source generators.

**DOM on enter:** all stages `.done` with counts; detail cards show timing breakdown and generated artifact list
**DOM on exit:** `resetAll()`

**Source symbols:** All generated files: `ReceptorRegistry.g.cs`, `MessageTypeRegistry.g.cs`, `DispatcherDelegates.g.cs`, `PerspectiveRunners.g.cs`, `TagRegistry.g.cs`, `MessageAssociations.g.cs`

**Intent:** Closes the loop with quantified impact. The generated file list shows all runtime artifacts that result from this pipeline.

---

## 5. Maintenance Guide

**Syntactic predicate implementation changes** (`src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs`):
- If the `ClassDeclarationSyntax` check changes → update step 2 code example
- If new syntactic checks are added → update step 2 detail card

**Semantic analysis checks change** (`src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs`):
- If `IReceptor<T>` interface check changes → update step 3 narration
- If new attributes are checked → update step 3 detail card

**Performance benchmarks change** (`src/Whizbang.Generators/ai-docs/performance-principles.md`):
- Steps 2 and 6 reference ~50ms (syntactic), ~200ms (semantic), ~275ms (total), ~5000ms (bad predicate)
- If benchmarks are updated → update these steps

**Generated output file names change**:
- Step 6 detail card lists: `ReceptorRegistry.g.cs`, `MessageTypeRegistry.g.cs`, `DispatcherDelegates.g.cs`, `PerspectiveRunners.g.cs`, `TagRegistry.g.cs`, `MessageAssociations.g.cs`
- If any generated file is added, removed, or renamed → update step 6

**`sealed record` requirement changes**:
- If Roslyn incremental generator caching mechanism changes → step 4 may need updating

**What does NOT require an update:**
- Changes to `IDispatcher`, `MessageHop`, `OutboxRecord`, `LifecycleStage`, or runtime types
- Changes to perspective, tag hook, or policy systems
- Changes to `process_work_batch` SQL
