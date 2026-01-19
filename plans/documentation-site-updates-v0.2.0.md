# Documentation Site Updates for v0.2.0

## Status

- **Created**: 2025-11-02
- **Target**: whizbang-lib.github.io documentation site
- **Version**: v0.2.0 (Streams, Policies & Observability)
- **Priority**: HIGH - Implementation complete, documentation needed

---

## Executive Summary

The v0.2.0 implementation is **complete** with 212 passing tests and 43 benchmarks. However, the documentation site does not reflect the actual implementation. This document outlines the gap between what was built and what is documented, and provides a plan to update the site.

**Key Issue**: The documentation site has proposals and future-looking content, but lacks documentation for the **actual v0.2.0 implementation** that exists in the codebase.

---

## Current Documentation Site Status

### What EXISTS on the site:

1. **proposals/policy-engine.md** - Complex policy proposal (NOT what we implemented)
2. **proposals/observability-metrics.md** - OpenTelemetry-focused proposal (NOT what we implemented)
3. **old-v1.0.0/** - Old documentation (outdated, doesn't match current implementation)
4. **v0.2.0/enhancements/** - Some enhancements, but not the core features we built
5. **v0.5.0/production/** - Future features (distributed event store, etc.)

### What is MISSING from the site:

1. ❌ **MessageEnvelope & MessageHop architecture** - Core observability model
2. ❌ **Hop-based context architecture** - Network packet-inspired design
3. ❌ **PolicyContext & PolicyDecisionTrail** - Actual policy implementation
4. ❌ **PolicyEngine implementation** - Simple first-match policy engine
5. ❌ **ISequenceProvider & InMemorySequenceProvider** - Sequence generation
6. ❌ **IPartitionRouter & HashPartitionRouter** - Partition routing
7. ❌ **IExecutionStrategy (SerialExecutor, ParallelExecutor)** - Execution strategies
8. ❌ **ITraceStore & InMemoryTraceStore** - Trace storage and querying
9. ❌ **SecurityContext** - User/tenant context
10. ❌ **Causation hop tracking** - Distributed tracing with parent hops
11. ❌ **UUIDv7 identity values** - Database-friendly IDs
12. ❌ **Caller information capture** - [CallerMemberName], etc.
13. ❌ **Time-travel debugging** - Using TraceStore for debugging
14. ❌ **Infrastructure mapping** - Kafka, Service Bus, RabbitMQ, Event Store

---

## Discrepancies Analysis

### 1. Policy Engine

**Documentation Site** (`proposals/policy-engine.md`):
- Complex policy system with canned policies, policy combinations, IDE integration
- Pre-execution actions, post-execution actions
- Policy versioning, hash generation
- IDE debugger integration
- State inspection interceptors

**Actual Implementation** (v0.2.0):
- Simple first-match policy engine
- Predicate-based matching (When/Then)
- PolicyConfiguration fluent API
- PolicyDecisionTrail for debugging
- PolicyContext with helper methods
- **Much simpler** than the proposal

**Action Required**: Create NEW documentation for the **actual** simple policy engine, move complex proposal to backlog.

### 2. Observability

**Documentation Site** (`proposals/observability-metrics.md`):
- OpenTelemetry integration
- W3C trace context
- Metric collection policies
- Custom fields and attributes
- Prometheus/Grafana integration

**Actual Implementation** (v0.2.0):
- MessageEnvelope with hops
- MessageHop with complete context snapshots
- Hop-based architecture (network packet analogy)
- Causation hop tracking
- Caller information capture
- SecurityContext
- PolicyDecisionTrail per hop
- Metadata stitching
- ITraceStore for time-travel debugging
- **Completely different** from the proposal

**Action Required**: Create NEW comprehensive observability documentation covering the hop-based architecture.

### 3. Execution Strategies

**Documentation Site**:
- References to "parallel execution" in v0.2.0/enhancements/dispatcher
- Parallel perspective execution

**Actual Implementation** (v0.2.0):
- IExecutionStrategy interface
- SerialExecutor (FIFO ordering with Channel-based queue)
- ParallelExecutor (concurrent execution with configurable limit)
- Lifecycle management (Start/Stop/Drain)
- CancellationToken support

**Action Required**: Create NEW documentation for execution strategies.

### 4. Sequence Providers

**Documentation Site**:
- No documentation found

**Actual Implementation** (v0.2.0):
- ISequenceProvider interface
- InMemorySequenceProvider (thread-safe, monotonic)
- Contract tests
- Benchmarks (single, batch, parallel, multi-stream)

**Action Required**: Create NEW documentation for sequence providers.

### 5. Partition Routers

**Documentation Site**:
- No documentation found

**Actual Implementation** (v0.2.0):
- IPartitionRouter interface
- HashPartitionRouter (FNV-1a hash, zero allocations)
- Consistent hashing
- Contract tests
- Benchmarks

**Action Required**: Create NEW documentation for partition routers.

---

## Documentation Structure Recommendation

### Location in Site

All v0.2.0 documentation should go in:
```
src/assets/docs/v0.2.0/
├── core/
│   ├── message-envelope.md          # MessageEnvelope & MessageHop
│   ├── hop-architecture.md           # Hop-based context architecture
│   ├── security-context.md           # SecurityContext (UserId/TenantId)
│   ├── identity-values.md            # UUIDv7 (MessageId, CorrelationId, CausationId)
│   └── caller-info.md                # Caller information capture
│
├── policies/
│   ├── policy-engine.md              # PolicyEngine overview
│   ├── policy-context.md             # PolicyContext API
│   ├── policy-configuration.md       # PolicyConfiguration fluent API
│   ├── policy-authoring.md           # Writing policies (patterns, best practices)
│   └── policy-decision-trail.md      # PolicyDecisionTrail debugging
│
├── observability/
│   ├── hop-based-tracing.md          # Complete hop-based tracing guide
│   ├── causation-tracking.md         # Causation hop tracking
│   ├── trace-store.md                # ITraceStore & InMemoryTraceStore
│   ├── time-travel-debugging.md      # Using TraceStore for debugging
│   └── metadata-stitching.md         # Metadata inheritance and stitching
│
├── execution/
│   ├── execution-strategies.md       # IExecutionStrategy overview
│   ├── serial-executor.md            # SerialExecutor (FIFO ordering)
│   ├── parallel-executor.md          # ParallelExecutor (concurrent)
│   └── lifecycle-management.md       # Start/Stop/Drain patterns
│
├── sequencing/
│   ├── sequence-providers.md         # ISequenceProvider overview
│   ├── in-memory-provider.md         # InMemorySequenceProvider
│   └── sequence-guarantees.md        # Monotonicity, thread-safety
│
├── partitioning/
│   ├── partition-routers.md          # IPartitionRouter overview
│   ├── hash-router.md                # HashPartitionRouter (FNV-1a)
│   └── partition-distribution.md     # Distribution quality, benchmarks
│
├── infrastructure/
│   ├── kafka-mapping.md              # Kafka/Event Hubs mapping
│   ├── service-bus-mapping.md        # Azure Service Bus mapping
│   ├── rabbitmq-mapping.md           # RabbitMQ mapping
│   └── event-store-mapping.md        # EventStoreDB mapping
│
├── tooling/                          # NOTE: Place in v0.4.0/ when extension releases
│   ├── vscode-extension.md           # VSCode extension overview
│   ├── installation.md               # Extension installation guide
│   ├── development-navigation.md     # GitLens-style navigation features
│   ├── runtime-debugging.md          # Runtime trace debugging features (requires v0.3.0+)
│   └── troubleshooting.md            # Common issues and solutions
│
└── guides/
    ├── getting-started.md            # Quick start with v0.2.0
    ├── tdd-workflow.md               # TDD approach (from docs/)
    └── migration-from-v0.1.md        # Migrating from v0.1.0
```

### Move Proposals

Complex proposals should be moved out of v0.2.0 context:
```
src/assets/docs/proposals/
├── policy-engine.md              # Move to backlog/ (too complex for v0.2.0)
├── observability-metrics.md      # Move to backlog/ (OpenTelemetry not in v0.2.0)
└── ... other proposals
```

---

## Content Sources

### From Library Repository (whizbang/)

We have comprehensive guides in `whizbang/docs/`:

1. **INFRASTRUCTURE-MAPPING.md** → Adapt for site as `infrastructure/*.md`
2. **VSCODE-EXTENSION-DATA.md** → Adapt for site as `observability/caller-info.md`
3. **TIME-TRAVEL-DEBUGGING.md** → Adapt for site as `observability/time-travel-debugging.md`
4. **POLICY-AUTHORING.md** → Adapt for site as `policies/policy-authoring.md`
5. **TDD-WORKFLOW.md** → Adapt for site as `guides/tdd-workflow.md`
6. **CALLER-INFO-CAPTURE.md** → Adapt for site as `core/caller-info.md`

### From VSCode Extension Plan (whizbang/)

Extract documentation from `plans/vscode-extension-plan.md`:

1. **tooling/vscode-extension.md** → Overview, features, benefits
   - Development-time navigation (GitLens-style)
   - Runtime debugging and visualization
   - Repository information (whizbang-vscode)
   - Installation from VS Code Marketplace

2. **tooling/installation.md** → Installation and setup
   - VS Code Marketplace installation
   - Extension requirements (.NET SDK)
   - Configuration options
   - Verifying installation

3. **tooling/development-navigation.md** → Development-time features
   - Message type annotations (dispatchers, receptors, perspectives)
   - Code lens providers
   - Hover tooltips
   - "Go to Dispatcher/Receptor/Perspective" commands
   - Cross-service navigation
   - Static flow diagrams

4. **tooling/runtime-debugging.md** → Runtime features (v0.3.0+)
   - Jump to source from traces
   - Visual message flow diagrams
   - Time-travel debugging
   - Live monitoring dashboard
   - Policy decision trail debugging

5. **tooling/troubleshooting.md** → Common issues
   - Extension not detecting messages
   - Roslyn analyzer build issues
   - Message registry not updating
   - Cross-service navigation not working

### From Implementation Code

Extract API documentation from:

1. **src/Whizbang.Core/Observability/**
   - MessageEnvelope.cs
   - MessageHop.cs
   - MessageTracing.cs
   - SecurityContext.cs
   - ITraceStore.cs
   - InMemoryTraceStore.cs

2. **src/Whizbang.Core/Policies/**
   - PolicyContext.cs
   - PolicyDecisionTrail.cs
   - IPolicyEngine.cs
   - PolicyEngine.cs
   - PolicyConfiguration.cs

3. **src/Whizbang.Core/Execution/**
   - IExecutionStrategy.cs
   - SerialExecutor.cs
   - ParallelExecutor.cs

4. **src/Whizbang.Core/Sequencing/**
   - ISequenceProvider.cs
   - InMemorySequenceProvider.cs

5. **src/Whizbang.Core/Partitioning/**
   - IPartitionRouter.cs
   - HashPartitionRouter.cs

### From Tests

Extract usage examples from:

1. **tests/Whizbang.Observability.Tests/**
   - MessageTracingTests.cs
   - SecurityContextTests.cs
   - TraceStore/TraceStoreContractTests.cs

2. **tests/Whizbang.Policies.Tests/**
   - PolicyContextTests.cs
   - PolicyEngineTests.cs

3. **tests/Whizbang.Execution.Tests/**
   - SerialExecutorTests.cs
   - ParallelExecutorTests.cs

4. **tests/Whizbang.Sequencing.Tests/**
   - InMemorySequenceProviderTests.cs

5. **tests/Whizbang.Partitioning.Tests/**
   - HashPartitionRouterTests.cs

### From Benchmarks

Performance data from:

1. **benchmarks/Whizbang.Benchmarks/**
   - ExecutorBenchmarks.cs
   - SequenceProviderBenchmarks.cs
   - PartitionRouterBenchmarks.cs
   - PolicyEngineBenchmarks.cs
   - TracingBenchmarks.cs
   - SimpleBenchmarks.cs

---

## Documentation Writing Guidelines

### Follow Site Standards

All documentation must follow the site's standards:

1. **Use site's editorconfig** (`CODE_SAMPLES.editorconfig`)
   - K&R/Egyptian braces (opening brace on same line)
   - 2-space indentation for code samples

2. **Version callouts**:
   ```markdown
   :::new
   New in v0.2.0: Hop-based observability architecture
   :::
   ```

3. **Code sample format**:
   ```markdown
   ```csharp{
     title: "Creating MessageEnvelope with Hops",
     category: "API",
     difficulty: "BEGINNER",
     tags: ["Observability", "MessageEnvelope", "Hops"],
     framework: "NET9"
   }
   // Code here
   ```
   ```

4. **Mermaid diagrams** where helpful:
   ```markdown
   ```mermaid
   sequenceDiagram
       participant A as API Gateway
       participant B as Orders Service
       A->>B: OrderCreatedEvent
   ```
   ```

5. **Interactive headers** (auto-generated anchors)

6. **Mobile-first design** (progressive disclosure)

---

## Priority Levels

### P0 - Critical (Must have for v0.2.0 release)

1. **Core Concepts**:
   - [ ] MessageEnvelope & MessageHop
   - [ ] Hop-based architecture overview
   - [ ] Policy Engine overview
   - [ ] Execution Strategies overview

2. **Getting Started**:
   - [ ] Quick start guide
   - [ ] Basic policy authoring
   - [ ] Basic observability usage

### P1 - High (Should have soon after release)

1. **Detailed API Docs**:
   - [ ] PolicyContext API reference
   - [ ] PolicyConfiguration API reference
   - [ ] ITraceStore API reference
   - [ ] ISequenceProvider API reference
   - [ ] IPartitionRouter API reference

2. **Guides**:
   - [ ] Time-travel debugging guide
   - [ ] Infrastructure mapping guide
   - [ ] TDD workflow guide

### P2 - Medium (Nice to have)

1. **Advanced Topics**:
   - [ ] Caller information capture details
   - [ ] Metadata stitching patterns
   - [ ] Causation hop tracking
   - [ ] Performance benchmarks

2. **Infrastructure-Specific**:
   - [ ] Kafka mapping details
   - [ ] Service Bus mapping details
   - [ ] RabbitMQ mapping details
   - [ ] Event Store mapping details

### P3 - Low (Future improvements / v0.4.0+)

1. **VSCode Extension** (v0.4.0+):
   - [ ] Extension overview and benefits
   - [ ] Installation guide
   - [ ] Development-time navigation features (GitLens-style)
   - [ ] Runtime debugging features (requires v0.3.0 persistent trace store)
   - [ ] Troubleshooting guide

2. **Future Features**:
   - [ ] Migration guides (v0.1 → v0.2, v0.2 → v0.3, etc.)
   - [ ] Best practices compendium
   - [ ] Performance tuning guide

---

## Implementation Plan

### Phase 1: Core Documentation (Week 1)

**Goal**: Minimum viable documentation for v0.2.0 release.

**Tasks**:
1. Create `v0.2.0/core/message-envelope.md`
2. Create `v0.2.0/core/hop-architecture.md`
3. Create `v0.2.0/policies/policy-engine.md`
4. Create `v0.2.0/execution/execution-strategies.md`
5. Create `v0.2.0/guides/getting-started.md`
6. Update navigation to include v0.2.0
7. Add version selector dropdown

**Success Criteria**:
- Users can understand core v0.2.0 concepts
- Users can write basic policies
- Users can use observability features
- Users can choose execution strategies

### Phase 2: Detailed API Documentation (Week 2)

**Goal**: Complete API reference for all v0.2.0 interfaces.

**Tasks**:
1. Create all `policies/*.md` files
2. Create all `observability/*.md` files
3. Create all `execution/*.md` files
4. Create all `sequencing/*.md` files
5. Create all `partitioning/*.md` files
6. Add code examples from tests
7. Add performance data from benchmarks

**Success Criteria**:
- Complete API coverage for all public interfaces
- Code examples for every major API
- Performance benchmarks documented

### Phase 3: Guides & Infrastructure (Week 3)

**Goal**: Comprehensive guides and infrastructure-specific documentation.

**Tasks**:
1. Create all `infrastructure/*.md` files
2. Create all `guides/*.md` files
3. Add diagrams (Mermaid)
4. Add cross-references between docs
5. SEO optimization (meta descriptions, structured data)
6. Mobile testing

**Success Criteria**:
- Complete infrastructure mapping
- Comprehensive debugging guide
- TDD workflow documented
- Site is mobile-friendly

### Phase 4: Polish & Review (Week 4)

**Goal**: High-quality, production-ready documentation.

**Tasks**:
1. Technical review of all content
2. Code sample verification (ensure all examples work)
3. Spelling/grammar check
4. Consistency check (terminology, formatting)
5. Screenshot/diagram review
6. Search index rebuild
7. Sitemap update

**Success Criteria**:
- Zero broken links
- All code samples verified
- Consistent terminology throughout
- Search works for all v0.2.0 topics

---

## GitHub Issues to Create

### Issue Template

```markdown
Title: [Docs] Add v0.2.0 documentation for [Component]

## Description

Add comprehensive documentation for [Component] in v0.2.0.

## Acceptance Criteria

- [ ] Create `src/assets/docs/v0.2.0/[category]/[component].md`
- [ ] Include API overview
- [ ] Include code examples (at least 3)
- [ ] Include usage patterns
- [ ] Include performance notes (from benchmarks)
- [ ] Follow site standards (CODE_SAMPLES.editorconfig)
- [ ] Add version callout (:::new)
- [ ] Add cross-references to related docs
- [ ] Test all code examples
- [ ] Mobile-friendly formatting

## Source Content

- Implementation: `src/Whizbang.Core/[path]`
- Tests: `tests/Whizbang.[Component].Tests/`
- Benchmarks: `benchmarks/Whizbang.Benchmarks/[Component]Benchmarks.cs`
- Guides: `docs/[COMPONENT].md` (if exists)

## Priority

[P0 / P1 / P2 / P3]

## Related Issues

- #[issue number]
```

### Issues to Create (25 total)

**P0 - Core Documentation** (5 issues):
1. [Docs] Add v0.2.0 MessageEnvelope & MessageHop documentation
2. [Docs] Add v0.2.0 Hop-based Architecture documentation
3. [Docs] Add v0.2.0 Policy Engine documentation
4. [Docs] Add v0.2.0 Execution Strategies documentation
5. [Docs] Add v0.2.0 Getting Started guide

**P1 - API Documentation** (10 issues):
6. [Docs] Add v0.2.0 PolicyContext API reference
7. [Docs] Add v0.2.0 PolicyConfiguration API reference
8. [Docs] Add v0.2.0 TraceStore API reference
9. [Docs] Add v0.2.0 SequenceProvider API reference
10. [Docs] Add v0.2.0 PartitionRouter API reference
11. [Docs] Add v0.2.0 SerialExecutor documentation
12. [Docs] Add v0.2.0 ParallelExecutor documentation
13. [Docs] Add v0.2.0 SecurityContext documentation
14. [Docs] Add v0.2.0 Caller Info Capture documentation
15. [Docs] Add v0.2.0 PolicyDecisionTrail documentation

**P1 - Guides** (3 issues):
16. [Docs] Add Time-Travel Debugging guide
17. [Docs] Add Infrastructure Mapping guide
18. [Docs] Add TDD Workflow guide

**P2 - Infrastructure** (2 issues):
19. [Docs] Add Infrastructure-specific mapping (Kafka, Service Bus, RabbitMQ, Event Store)
20. [Docs] Add Performance Benchmarks documentation

**P3 - VSCode Extension** (5 issues, v0.4.0+):
21. [Docs] Add VSCode Extension overview and benefits
22. [Docs] Add VSCode Extension installation guide
23. [Docs] Add VSCode Extension development-time navigation features
24. [Docs] Add VSCode Extension runtime debugging features
25. [Docs] Add VSCode Extension troubleshooting guide

---

## Metrics for Success

### Documentation Coverage

Target: **100% coverage** of v0.2.0 public APIs

- [ ] All public interfaces documented
- [ ] All public classes documented
- [ ] All public methods documented
- [ ] All public properties documented

### Code Example Coverage

Target: **3+ examples per major API**

- [ ] MessageEnvelope: 5+ examples
- [ ] PolicyEngine: 5+ examples
- [ ] IExecutionStrategy: 3+ examples per implementation
- [ ] ITraceStore: 5+ examples
- [ ] ISequenceProvider: 3+ examples
- [ ] IPartitionRouter: 3+ examples

### User Journey Coverage

Target: **Complete user journeys** for common tasks

- [ ] "Hello World" (simplest possible usage)
- [ ] "Policy-based routing" (common pattern)
- [ ] "Time-travel debugging" (power user feature)
- [ ] "Infrastructure migration" (Kafka → Service Bus)

### Quality Metrics

- [ ] Zero broken links
- [ ] All code samples compile and run
- [ ] Consistent terminology (glossary)
- [ ] Mobile-friendly (responsive design)
- [ ] SEO-optimized (meta tags, structured data)
- [ ] Search-indexed (all content searchable)

---

## Risks & Mitigation

### Risk 1: Documentation Drift

**Risk**: Documentation gets out of sync with implementation.

**Mitigation**:
- Generate API docs from XML comments (future)
- Test all code examples in CI/CD
- Regular review cycle (quarterly)

### Risk 2: Overwhelming Users

**Risk**: Too much documentation overwhelms new users.

**Mitigation**:
- Progressive disclosure (beginner → advanced)
- Clear difficulty labels (BEGINNER, INTERMEDIATE, ADVANCED)
- "Getting Started" path for new users
- "Quick Reference" for experienced users

### Risk 3: Incomplete Migration

**Risk**: Users confused by old docs vs. new docs.

**Mitigation**:
- Clear version callouts (:::new, :::updated, :::deprecated)
- Version selector dropdown
- Migration guide from v0.1.0 to v0.2.0
- Archive old docs clearly labeled

---

## Next Steps

1. **Review this plan** with Phil
2. **Create GitHub issues** (20 total)
3. **Assign priorities** (P0, P1, P2, P3)
4. **Start Phase 1** (Core Documentation)
5. **Set timeline** (4-week plan)

---

## Appendix: Quick Reference

### v0.2.0 Implementation Summary

**Tests**: 212 passing
- Observability: 98 tests
- Core: 41 tests
- Policies: 33 tests
- Sequencing: 12 tests
- Partitioning: 8 tests
- Execution: 20 tests

**Benchmarks**: 43 benchmarks
- ExecutorBenchmarks: 4
- SequenceProviderBenchmarks: 5
- PartitionRouterBenchmarks: 5
- PolicyEngineBenchmarks: 7
- TracingBenchmarks: 12
- SimpleBenchmarks: 10

**Documentation Guides** (in `whizbang/docs/`):
1. INFRASTRUCTURE-MAPPING.md
2. VSCODE-EXTENSION-DATA.md
3. TIME-TRAVEL-DEBUGGING.md
4. POLICY-AUTHORING.md
5. TDD-WORKFLOW.md
6. CALLER-INFO-CAPTURE.md

**Key Features**:
- Hop-based observability architecture
- Policy-driven routing
- Execution strategies (Serial, Parallel)
- Sequence providers
- Partition routers
- Trace store for time-travel debugging
- UUIDv7 identity values
- Caller information capture
- Causation hop tracking
- SecurityContext

---

## Changelog

### 2025-11-02 - VSCode Extension Documentation Added

**Added**:
- **tooling/** directory structure for VSCode extension documentation (v0.4.0+)
  - `vscode-extension.md` - Extension overview and benefits
  - `installation.md` - Installation and setup guide
  - `development-navigation.md` - GitLens-style navigation features
  - `runtime-debugging.md` - Runtime debugging and visualization
  - `troubleshooting.md` - Common issues and solutions

**Updated**:
- **Content Sources** - Added "From VSCode Extension Plan" section with 5 documentation sources
- **Priority Levels** - Expanded P3 to include 5 VSCode extension documentation items
- **GitHub Issues** - Increased from 20 to 25 total issues (added 5 for VSCode extension)
- **Documentation Structure** - Added note about placing tooling docs in v0.4.0/ when extension releases

**Key Additions**:
1. Complete VSCode extension documentation structure
2. Separate runtime features (requires v0.3.0+) from development-time features (works with v0.2.0+)
3. Cross-references to `plans/vscode-extension-plan.md` for implementation details
4. Versioning guidance for tooling documentation
