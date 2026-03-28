# XML Documentation Completion Plan

## Context

`GenerateDocumentationFile` has been enabled in Directory.Build.props, but 3,304 public members lack XML doc comments (CS1591). These are temporarily suppressed via NoWarn. This plan fixes all gaps so IDEs show rich IntelliSense for every Whizbang type.

## Scope

3,304 CS1591 errors across ~40 files in Whizbang.Core. Breakdown:

| File | Missing | Notes |
|------|---------|-------|
| IPerspectiveFor.cs | 2,548 | 20 generic variants x ~127 Apply methods each |
| ILensQuery.cs | 112 | 10 generic variants |
| ITemporalPerspectiveFor.cs | 108 | 10 variants |
| IPerspectiveWithActionsFor.cs | 108 | 10 variants |
| TransportMetrics.cs | 44 | OTel instruments |
| DispatcherMetrics.cs | 44 | OTel instruments |
| WorkCoordinatorMetrics.cs | 38 | OTel instruments |
| AuditEventModel.cs | 32 | Audit system events |
| PerspectiveMetrics.cs | 30 | OTel instruments |
| SystemEvents.cs | 28 | System event records |
| LifecycleCoordinatorMetrics.cs | 24 | OTel instruments |
| LifecycleMetrics.cs | 22 | OTel instruments |
| IGlobalPerspectiveFor.cs | 20 | 3 variants |
| ~25 smaller files | ~146 | Various |

## Strategy

### Batch 1: Generic Interface Variants (2,896 errors)
IPerspectiveFor, ILensQuery, ITemporalPerspectiveFor, IPerspectiveWithActionsFor, IGlobalPerspectiveFor

These are generated from templates — each variant has identical doc patterns. Write a script to add `/// <inheritdoc/>` or generate summary+param docs from the base variant.

**Approach**: For each generic variant (IPerspectiveFor<TModel, TEvent1, TEvent2> etc.):
- The base variant (1 type param) should have full docs
- Variants 2-20 should use `/// <inheritdoc/>` to inherit from the base
- The Apply methods on each variant are identical in purpose, just different type params

### Batch 2: Metrics Classes (200+ errors)
All public Counter/Histogram/UpDownCounter properties need `/// <summary>` matching their instrument descriptions.

### Batch 3: System Events + Audit (60 errors)
All event record properties and enum values need docs.

### Batch 4: Remaining (~150 errors)
Various smaller files — strategy, executor, transport, messaging types.

## Verification

After all batches:
1. Remove CS1591/CS1570/CS1573 from NoWarn in Directory.Build.props
2. `dotnet build` — should compile with 0 errors
3. Check `bin/Debug/net10.0/Whizbang.Core.xml` exists and contains all docs
4. In VS/Rider, hover over IDispatcher — verify IntelliSense shows full docs

## Notes
- The `<docs>` and `<tests>` custom tags should be preserved (they drive the code-docs-map)
- New `<seealso>` tags with docs site URLs should be added to key types
- `<example>` blocks should be added to at least: IDispatcher methods, Route factory methods, LifecycleStage enum values, FireAtAttribute
