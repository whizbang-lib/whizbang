# Application Health Runbook

> Comprehensive operational reference for monitoring and troubleshooting Whizbang-based applications.
> Backend-agnostic — works with any OTLP-compatible observability stack (Prometheus/Grafana, Datadog, Azure Monitor, Jaeger, .NET Aspire Dashboard, etc.).

---

## Table of Contents

1. [OpenTelemetry Configuration](#1-opentelemetry-configuration)
2. [Health Check Endpoints](#2-health-check-endpoints)
3. [Application-Level Metrics & Traces](#3-application-level-metrics--traces)
4. [Background Worker Monitoring](#4-background-worker-monitoring)
5. [Infrastructure Monitoring](#5-infrastructure-monitoring)
6. [Audit & Compliance](#6-audit--compliance)
7. [Key Operational Scenarios (Troubleshooting)](#7-key-operational-scenarios-troubleshooting)
8. [Quick Reference](#8-quick-reference)

---

## 1. OpenTelemetry Configuration

### Registering Whizbang ActivitySources

Whizbang exposes five `ActivitySource` instances that must be registered with your OpenTelemetry SDK. Without registration, no spans are collected.

```csharp
// In Program.cs or your service defaults
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Whizbang.Execution")    // Dispatch & executor spans
        .AddSource("Whizbang.Transport")    // Transport send/receive spans
        .AddSource("Whizbang.Hosting")      // Topic/subscription/worker init spans
        .AddSource("Whizbang.Tracing")      // Handler invocation spans
        .AddSource("Whizbang.MessageTags")  // Metric & telemetry tag spans
    )
    .WithMetrics(metrics => metrics
        .AddMeter("Whizbang.MessageTags")   // Metric tag counters/histograms
    );
```

All sources use version `"1.0.0"`.

> **Source**: `src/Whizbang.Core/Observability/WhizbangActivitySource.cs`, `src/Whizbang.Observability/Hooks/OpenTelemetrySpanHook.cs`

### Configuring the OTLP Exporter

Set the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable to enable OTLP export:

```bash
# Local development (e.g., Aspire Dashboard)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317

# Production (e.g., Grafana Tempo, Datadog)
OTEL_EXPORTER_OTLP_ENDPOINT=https://otel-collector.internal:4317
```

In code, the exporter activates automatically when the environment variable is set:

```csharp
var useOtlpExporter = !string.IsNullOrWhiteSpace(
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

if (useOtlpExporter) {
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}
```

> **Source**: `samples/ECommerce/ECommerce.ServiceDefaults/Extensions.cs`

### TracingOptions Configuration

Whizbang tracing is controlled via `TracingOptions`, configurable programmatically or via `appsettings.json`.

**Programmatic:**

```csharp
services.AddWhizbang(options => {
    options.Tracing.Verbosity = TraceVerbosity.Verbose;
    options.Tracing.Components = TraceComponents.All;
    options.Tracing.EnableOpenTelemetry = true;
    options.Tracing.EnableStructuredLogging = true;
});
```

**Via appsettings.json:**

```json
{
  "Whizbang": {
    "Tracing": {
      "Verbosity": "Verbose",
      "Components": "All",
      "EnableOpenTelemetry": true,
      "EnableStructuredLogging": true,
      "EnableWorkerBatchSpans": false,
      "EnablePerspectiveEventSpans": false,
      "TracedHandlers": {
        "OrderReceptor": "Debug",
        "Payment*": "Verbose"
      },
      "TracedMessages": {
        "ReseedSystemEvent": "Debug",
        "*Command": "Normal"
      }
    }
  }
}
```

**TracedHandlers / TracedMessages** support pattern matching:
- Exact match: `"OrderReceptor"`
- Wildcard: `"Payment*"` matches `PaymentHandler`, `PaymentValidator`, etc.
- Namespace wildcard: `"MyApp.Orders.*"` matches all handlers in that namespace
- Suffix match: `"OrderReceptor"` matches `MyApp.Handlers.OrderReceptor`

> **Source**: `src/Whizbang.Core/Tracing/TracingOptions.cs`, `src/Whizbang.Core/Tracing/Tracer.cs`

---

## 2. Health Check Endpoints

### Built-in Endpoints

| Endpoint | Purpose | Probe Type |
|----------|---------|------------|
| `/health` | All health checks must pass | **Readiness** |
| `/alive` | Only checks tagged `"live"` | **Liveness** |

These are mapped via `MapDefaultEndpoints()`:

```csharp
app.MapDefaultEndpoints();
```

A default liveness check (`"self"`) is always registered:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
```

> **Note**: In the sample app, health check endpoints are only mapped in development. For production Kubernetes deployments, remove the `IsDevelopment()` guard.

### Transport Health Checks

**RabbitMQ** — Checks that the `IConnection` is open:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<RabbitMQHealthCheck>("rabbitmq");
```

Returns:
- `Healthy` — Connection is open
- `Degraded` — Transport is not a RabbitMQ transport
- `Unhealthy` — Connection is not open

> **Source**: `src/Whizbang.Transports.RabbitMQ/RabbitMQHealthCheck.cs`

**Azure Service Bus** — Checks transport availability:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<AzureServiceBusHealthCheck>("azureservicebus");
```

Returns:
- `Healthy` — Transport is available
- `Degraded` — Transport is not Azure Service Bus
- `Unhealthy` — Exception during check

> **Source**: `src/Whizbang.Transports.AzureServiceBus/AzureServiceBusHealthCheck.cs`

### Adding Custom Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"])
    .AddCheck<RabbitMQHealthCheck>("rabbitmq")
    .AddNpgSql(connectionString, name: "postgresql")
    .AddCheck("external-api", () => {
        // Check external dependency
        return HealthCheckResult.Healthy();
    });
```

### Kubernetes Probe Configuration

```yaml
livenessProbe:
  httpGet:
    path: /alive
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10
  failureThreshold: 3

readinessProbe:
  httpGet:
    path: /health
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 15
  failureThreshold: 3
```

---

## 3. Application-Level Metrics & Traces

### 3a. Message Flow Monitoring

#### Metric Tags (`[MetricTag]`)

Developers declare metric tags on message types. The source generator discovers them at compile time, and `OpenTelemetryMetricHook` records them via the `Whizbang.MessageTags` meter (v1.0.0).

**How developers declare them:**

```csharp
// Counter — increments by 1 for each event
[MetricTag(
    Tag = "order-created",
    MetricName = "orders.created",
    Type = MetricType.Counter,
    Properties = ["TenantId", "Region"])]
public sealed record OrderCreatedEvent(Guid OrderId, string TenantId, string Region);

// Histogram — records the TotalAmount value
[MetricTag(
    Tag = "order-amount",
    MetricName = "orders.amount",
    Type = MetricType.Histogram,
    ValueProperty = nameof(TotalAmount),
    Unit = "USD",
    Properties = ["TenantId"])]
public sealed record OrderCompletedEvent(Guid OrderId, decimal TotalAmount, string TenantId);
```

**What ops sees in the metrics backend:**

| Metric Name | Type | Dimensions | Description |
|-------------|------|------------|-------------|
| `orders.created` | Counter | `tenantid`, `region` | Incremented per `OrderCreatedEvent` |
| `orders.amount` | Histogram | `tenantid` | Records `TotalAmount` per `OrderCompletedEvent` |

**Supported metric types:**
- `MetricType.Counter` — Counting occurrences (default value = 1)
- `MetricType.Histogram` — Measuring distributions (requires `ValueProperty`)
- `MetricType.Gauge` — Point-in-time values (requires `ValueProperty`)

Dimensions are extracted from the `Properties` array (payload fields) and scope values (e.g., TenantId from security context). Property names are lowercased for tag keys.

> **Source**: `src/Whizbang.Core/Attributes/MetricTagAttribute.cs`, `src/Whizbang.Observability/Hooks/OpenTelemetryMetricHook.cs`

#### Telemetry Tags (`[TelemetryTag]`)

Telemetry tags create or enrich OpenTelemetry spans via the `Whizbang.MessageTags` ActivitySource.

```csharp
[TelemetryTag(
    Tag = "payment-processed",
    Properties = ["PaymentId", "Amount", "Currency"],
    SpanName = "ProcessPayment",
    Kind = SpanKind.Internal)]
public sealed record PaymentProcessedEvent(Guid PaymentId, decimal Amount, string Currency);
```

**Span attributes set by the hook:**
- `messaging.system` = `"whizbang"`
- `messaging.operation` = `"process"`
- `whizbang.tag` = the tag value
- `whizbang.message_type` = fully qualified type name
- `whizbang.scope.*` = scope values (tenant, user, etc.)
- `whizbang.payload.*` = extracted payload properties

**SpanKind mapping:**
| SpanKind | Use Case |
|----------|----------|
| `Internal` | Local operations (default) |
| `Server` | Processing incoming requests |
| `Client` | Making outgoing requests |
| `Producer` | Publishing messages |
| `Consumer` | Consuming messages |

When `RecordAsEvent = true` (default), the message is also recorded as an `ActivityEvent` on the span.

> **Source**: `src/Whizbang.Core/Attributes/TelemetryTagAttribute.cs`, `src/Whizbang.Observability/Hooks/OpenTelemetrySpanHook.cs`

### 3b. Handler Execution

The `Tracer` (`ITracer`) emits spans for every handler invocation via the `Whizbang.Tracing` ActivitySource.

**Span name format:** `Handler: {ClassName.MethodName}`

**Span attributes:**

| Attribute | Description |
|-----------|-------------|
| `whizbang.handler.name` | Fully qualified handler name |
| `whizbang.message.type` | Message type being handled |
| `whizbang.handler.count` | Number of handlers registered for this message |
| `whizbang.trace.explicit` | Whether this handler was explicitly traced (via config or `[WhizbangTrace]`) |
| `whizbang.handler.status` | `Success`, `Failed`, or `EarlyReturn` |
| `whizbang.handler.duration_ms` | Handler execution time in milliseconds |

**On failure**, the span also includes:
- `ActivityStatusCode.Error` with the exception message
- An `exception` event with `exception.type`, `exception.message`, and `exception.stacktrace`

**Structured log messages:**

| Level | Format | When |
|-------|--------|------|
| `Information` | `[TRACE] Handler invocation: {Name} for {Type} ({Count} handlers) - explicit via [WhizbangTrace]` | Explicit handler begin |
| `Debug` | `[trace] Handler invocation: {Name} for {Type} ({Count} handlers)` | Normal handler begin |
| `Information` | `[TRACE] Handler completed: {Name} for {Type} - {Status} in {Duration}ms - explicit` | Explicit handler end |
| `Debug` | `[trace] Handler completed: {Name} for {Type} - {Status} in {Duration}ms` | Normal handler end |
| `Error` | `[TRACE] Handler FAILED: {Name} for {Type} after {Duration}ms` | Handler failure |

> **Source**: `src/Whizbang.Core/Tracing/Tracer.cs`

### 3c. Message Envelope & Hop Tracing

Every message in Whizbang is wrapped in a `MessageEnvelope<TMessage>` that carries full tracing context.

**Envelope fields:**

| Field | Description |
|-------|-------------|
| `MessageId` | Unique identifier for this message |
| `Payload` | The actual message (strongly typed) |
| `Hops` | Ordered list of processing hops |

**MessageHop fields:**

| Field | Description |
|-------|-------------|
| `Type` | `Current` (this message) or `Causation` (from parent) |
| `ServiceInstance` | Service name, instance ID, hostname, process ID |
| `Timestamp` | When this hop occurred |
| `Topic` | Topic at this hop |
| `StreamId` | Event stream at this hop |
| `PartitionIndex` | Partition index (if applicable) |
| `SequenceNumber` | Sequence number (if applicable) |
| `ExecutionStrategy` | e.g., `"SerialExecutor"`, `"ParallelExecutor"` |
| `SecurityContext` | User ID, tenant ID at this hop |
| `Metadata` | Key-value metadata (JSON values) |
| `Trail` | Policy decisions made at this hop |
| `CallerMemberName` | Source method name |
| `CallerFilePath` | Source file path |
| `CallerLineNumber` | Source line number |
| `Duration` | Processing time for this hop |
| `TraceParent` | W3C Trace Context header for distributed tracing |
| `CausationId` | Parent message ID (for causation hops) |
| `CorrelationId` | Correlation ID for grouping related messages |

**W3C Trace Context (`traceparent`):**
The `TraceParent` field on each hop contains the W3C traceparent header value (e.g., `00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01`). This enables cross-service correlation when messages traverse service boundaries.

**ITraceStore queries:**

| Method | Description |
|--------|-------------|
| `GetByMessageIdAsync(messageId)` | Look up a specific message |
| `GetByCorrelationAsync(correlationId)` | Find all messages in a correlation group |
| `GetCausalChainAsync(messageId)` | Walk the full causal chain (parents + children) |
| `GetByTimeRangeAsync(from, to)` | Find messages within a time window |

> **Source**: `src/Whizbang.Core/Observability/MessageEnvelope.cs`, `src/Whizbang.Core/Observability/MessageHop.cs`, `src/Whizbang.Core/Observability/ITraceStore.cs`

### 3d. Policy Decision Trail

Every policy evaluation is recorded in a `PolicyDecisionTrail` attached to the message hop.

**PolicyDecision fields:**

| Field | Description |
|-------|-------------|
| `PolicyName` | e.g., `"StreamSelection"`, `"ExecutionStrategy"` |
| `Rule` | The rule evaluated (e.g., `"Order.* → order-{id}"`) |
| `Matched` | Whether the rule matched |
| `Configuration` | Applied configuration (stream key, executor type, etc.) |
| `Reason` | Human-readable explanation |
| `Timestamp` | When the decision was made |

**Querying policy decisions:**

```csharp
// Get all policy decisions for a message
var decisions = envelope.GetAllPolicyDecisions();

// Filter to matched rules only
var trail = hop.Trail;
var matched = trail.GetMatchedRules();
var unmatched = trail.GetUnmatchedRules();
```

Use this to debug routing issues: if a message is going to the wrong stream or using the wrong executor, inspect the policy trail to see which rules matched and why.

> **Source**: `src/Whizbang.Core/Policies/PolicyDecisionTrail.cs`

### 3e. Failure Classification

The `MessageFailureReason` enum classifies why a message failed processing:

| Value | Int | Description | Typical Response |
|-------|-----|-------------|------------------|
| `None` | 0 | No failure (success) | — |
| `TransportNotReady` | 1 | Transport not connected; message buffered | Check transport connectivity |
| `TransportException` | 2 | Broker connectivity/service issue | Check message broker health |
| `SerializationError` | 3 | JSON serialization/deserialization failed | Check message schema compatibility |
| `ValidationError` | 4 | Message fails validation | Check message payload |
| `MaxAttemptsExceeded` | 5 | Retry limit exhausted | Investigate root cause of repeated failures |
| `LeaseExpired` | 6 | Message held too long in buffer | Check worker health, increase lease |
| `EventStorageFailure` | 7 | Event store persistence failed | Check database connectivity and capacity |
| `Unknown` | 99 | Unclassified error | Investigate logs |

**Alerting recommendations:**
- `TransportNotReady` / `TransportException` — Alert on sustained occurrences (transient spikes during deploys are normal)
- `SerializationError` — Alert immediately (likely a breaking schema change)
- `MaxAttemptsExceeded` — Alert and investigate (something is persistently failing)
- `EventStorageFailure` — Alert immediately (data loss risk)
- `Unknown` — Alert and investigate

> **Source**: `src/Whizbang.Core/Messaging/MessageFailureReason.cs`

---

## 4. Background Worker Monitoring

### 4a. WorkCoordinatorPublisherWorker

**Purpose:** Outbox publishing with lease-based coordination. Polls the database for outbox messages, publishes them to the transport, and marks them as completed.

**Configuration (`WorkCoordinatorPublisherOptions`):**

| Setting | Default | Description |
|---------|---------|-------------|
| `PollingIntervalMilliseconds` | `1000` | How often to poll for new work |
| `LeaseSeconds` | `300` | Lease duration for claimed work items |
| `StaleThresholdSeconds` | `600` | When to reclaim work from unresponsive instances |
| `PartitionCount` | `10000` | Number of hash partitions for work distribution |
| `IdleThresholdPolls` | `2` | Consecutive empty polls before entering idle state |

**What to watch:**

| Metric / Signal | What It Means | Action |
|-----------------|---------------|--------|
| Consecutive `TransportNotReady` buffers | Transport is down, messages accumulating | Check transport health check |
| High buffered message count | Messages not being published | Check transport connectivity |
| Stale lease recovery events | An instance died without completing work | Normal during deploys; investigate if persistent |
| `DatabaseNotReady` checks | Database unreachable | Check PostgreSQL connectivity |
| Idle → Active transitions | Work appearing after idle period | Normal during variable load |

> **Source**: `src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs`

### 4b. TransportConsumerWorker

**Purpose:** Receives events from RabbitMQ or Azure Service Bus, deserializes them, and routes to receptors for processing.

**What to watch:**

| Signal | What It Means | Action |
|--------|---------------|--------|
| Message receive rate dropping | Consumer may be disconnected | Check transport health check |
| Deserialization errors | Schema mismatch between producer and consumer | Check message type registration |
| Transport reconnections | Broker instability | Check RabbitMQ/Service Bus health |
| High processing latency | Handlers are slow | Profile handler execution traces |

> **Source**: `src/Whizbang.Core/Workers/TransportConsumerWorker.cs`

### 4c. PerspectiveWorker

**Purpose:** Event replay for read models (perspectives) with checkpoint tracking. Polls the event store for new events, replays them through perspective handlers, and tracks progress.

**Configuration (`PerspectiveWorkerOptions`):**

| Setting | Default | Description |
|---------|---------|-------------|
| `PollingIntervalMilliseconds` | `1000` | How often to poll for new events |
| `LeaseSeconds` | `300` | Lease duration for perspective processing |
| `StaleThresholdSeconds` | `600` | When to reclaim from unresponsive instances |
| `PartitionCount` | `10000` | Hash partitions for work distribution |
| `IdleThresholdPolls` | `2` | Consecutive empty polls before idle state |
| `PerspectiveBatchSize` | `100` | Events to process per batch |

**What to watch:**

| Signal | What It Means | Action |
|--------|---------------|--------|
| Growing perspective lag | Perspectives falling behind event store | Increase batch size, check query performance |
| Checkpoint not advancing | Perspective processing is stuck | Check handler errors, database connectivity |
| Consecutive empty polls → idle | No new events to process | Normal during low traffic |
| Database not ready | Event store unreachable | Check PostgreSQL connectivity |
| High event processing duration | Perspective handlers are slow | Profile with handler traces |

**Perspective lag** is the distance between the latest event in the event store and the perspective's last processed checkpoint. Monitor this by comparing the checkpoint position against the event store's latest sequence number.

> **Source**: `src/Whizbang.Core/Workers/PerspectiveWorker.cs`

---

## 5. Infrastructure Monitoring

### 5a. PostgreSQL

| Metric | Why It Matters | How to Monitor |
|--------|---------------|----------------|
| Connection pool utilization | Pool exhaustion causes request failures | `Npgsql` metrics, `pg_stat_activity` |
| Query latency | Slow queries impact handler duration | EF Core command logging at `Warning` level |
| Database size & growth rate | Capacity planning | `pg_database_size()` |
| Replication lag | Read replicas falling behind | `pg_stat_replication` |
| Event store table sizes | `wh_event_store` can grow rapidly | `pg_total_relation_size()` |

**Recommended log levels:**

```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Transaction": "Warning",
      "Microsoft.EntityFrameworkCore.Infrastructure": "Warning"
    }
  }
}
```

Setting `Database.Command` to `Warning` logs only slow or failed queries, which is ideal for production monitoring without excessive noise.

### 5b. RabbitMQ / Azure Service Bus

| Metric | Why It Matters | How to Monitor |
|--------|---------------|----------------|
| Queue depth | Messages accumulating faster than consumed | RabbitMQ Management API / Azure Monitor |
| Consumer count | Lost consumers mean no processing | RabbitMQ Management API / Azure Monitor |
| Dead letter queue depth | Failed messages accumulating | Monitor DLQ queues specifically |
| Connection/channel health | Broken connections halt message flow | Whizbang health checks + broker metrics |
| Message throughput | Understand normal baseline for anomaly detection | Broker metrics |
| Publish-to-consume latency | End-to-end message processing time | Custom metrics or distributed traces |

### 5c. Container / Host

| Metric | Why It Matters | How to Monitor |
|--------|---------------|----------------|
| CPU utilization per service | Saturation indicates scaling need | Container runtime metrics |
| Memory utilization | OOM kills cause data loss | Container runtime metrics |
| GC pressure (Gen0/Gen1/Gen2) | High Gen2 collections indicate memory pressure | .NET runtime metrics (`dotnet-counters`) |
| Thread pool starvation | Sync-over-async or thread pool exhaustion | `ThreadPool.PendingWorkItemCount`, `ThreadPool.ThreadCount` |
| Process restart count | Frequent restarts indicate instability | Container orchestrator metrics |

Enable .NET runtime instrumentation for GC and thread pool metrics:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddRuntimeInstrumentation()  // GC, thread pool, etc.
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
    );
```

---

## 6. Audit & Compliance

### AuditLogEntry Structure

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique audit entry identifier |
| `StreamId` | `string` | Event stream (e.g., `"Order-abc123"`) |
| `StreamPosition` | `long` | Position within the stream |
| `EventType` | `string` | Event type name (e.g., `"OrderCreated"`) |
| `Timestamp` | `DateTimeOffset` | When the event was recorded |
| `TenantId` | `string?` | Tenant identifier (from event scope) |
| `UserId` | `string?` | User identifier (from event scope) |
| `UserName` | `string?` | User display name (from event scope) |
| `CorrelationId` | `string?` | Distributed tracing correlation |
| `CausationId` | `string?` | Triggering event/command link |
| `Body` | `JsonElement` | Full event body as JSON |
| `AuditReason` | `string?` | Reason for auditing (from `[AuditEvent]`) |

### Compliance Queries

```csharp
// Who changed entity X?
var entityHistory = await auditLens.QueryAsync(q => q
    .Where(a => a.StreamId == $"Order-{orderId}")
    .OrderBy(a => a.StreamPosition));

// What did user Y modify?
var userChanges = await auditLens.QueryAsync(q => q
    .Where(a => a.UserId == userId)
    .Where(a => a.Timestamp >= startDate)
    .OrderByDescending(a => a.Timestamp));

// All changes in tenant Z during a time window
var tenantActivity = await auditLens.QueryAsync(q => q
    .Where(a => a.TenantId == tenantId)
    .Where(a => a.Timestamp >= from && a.Timestamp <= to)
    .OrderBy(a => a.Timestamp));
```

### Causation Chain Traversal for Incident Analysis

Use `ITraceStore` to walk the causal chain from any message:

```csharp
// Find the full causal chain for a message involved in an incident
var chain = await traceStore.GetCausalChainAsync(messageId);

// Each envelope in the chain includes:
// - The message payload
// - All hops (with timestamps, service instances, policy decisions)
// - Causation and correlation IDs
foreach (var envelope in chain) {
    var decisions = envelope.GetAllPolicyDecisions();
    var correlationId = envelope.GetCorrelationId();
    var causationId = envelope.GetCausationId();
    // Reconstruct the sequence of events
}
```

> **Source**: `src/Whizbang.Core/Audit/AuditLogEntry.cs`, `src/Whizbang.Core/Observability/ITraceStore.cs`

---

## 7. Key Operational Scenarios (Troubleshooting)

### Messages not being processed

```
WorkCoordinatorPublisher health
  └─ Is the worker running? Check BackgroundService logs
  └─ Is the database ready? Check DatabaseReadiness logs
  └─ Are messages in the outbox? Query wh_outbox table

Transport connectivity
  └─ Check /health endpoint
  └─ Check RabbitMQHealthCheck / AzureServiceBusHealthCheck status
  └─ Is the connection open? Check transport logs

Handler failures
  └─ Check Whizbang.Tracing spans for Failed status
  └─ Check structured logs for [TRACE] Handler FAILED messages
  └─ Check MessageFailureReason for failure classification
```

### Perspective lag growing

```
PerspectiveWorker health
  └─ Is the worker running? Check BackgroundService logs
  └─ Is IdleThresholdPolls being hit? Check consecutive empty polls
  └─ Is batch size sufficient? Consider increasing PerspectiveBatchSize

Event store query performance
  └─ Check EF Core command logs for slow queries
  └─ Check PostgreSQL query latency metrics
  └─ Are event store tables properly indexed?

Checkpoint status
  └─ Is the checkpoint advancing? Query perspective checkpoint table
  └─ Are there handler errors blocking progress?
```

### High latency on commands

```
Handler trace durations
  └─ Check Whizbang.Tracing spans for handler.duration_ms
  └─ Which handler is slowest?

Policy evaluation time
  └─ Check PolicyDecisionTrail timestamps
  └─ Are there many unmatched rules being evaluated?

Database query latency
  └─ Check EF Core command logs
  └─ Check PostgreSQL connection pool utilization
  └─ Are there lock contention issues?
```

### Transport disconnections

```
Health check endpoints
  └─ Is /health returning Unhealthy?
  └─ Which health check is failing?

Transport health check
  └─ RabbitMQ: Is IConnection.IsOpen false?
  └─ Azure Service Bus: Is the transport disposed?

Connection string validity
  └─ Has the connection string expired or rotated?
  └─ Is the broker reachable from this network?
  └─ DNS resolution working?
```

### Distributed trace gaps

```
ActivitySource registration
  └─ Are all 5 sources registered? (Execution, Transport, Hosting, Tracing, MessageTags)
  └─ Missing sources = missing spans

OTLP export
  └─ Is OTEL_EXPORTER_OTLP_ENDPOINT set?
  └─ Is the collector reachable?
  └─ Check for export errors in logs

W3C traceparent propagation
  └─ Are MessageHop.TraceParent values populated?
  └─ Is Activity.Current set during message processing?
  └─ Cross-service: Is traceparent header being forwarded?
```

---

## 8. Quick Reference

### ActivitySource Names

| ActivitySource Name | Version | What It Covers |
|--------------------|---------|----------------|
| `Whizbang.Execution` | 1.0.0 | Dispatch operations, SerialExecutor/ParallelExecutor spans |
| `Whizbang.Transport` | 1.0.0 | Transport send/receive operations |
| `Whizbang.Hosting` | 1.0.0 | Topic/subscription creation, filter provisioning, worker init |
| `Whizbang.Tracing` | 1.0.0 | Handler invocation spans (begin/end with status) |
| `Whizbang.MessageTags` | 1.0.0 | Telemetry tag spans (from `[TelemetryTag]` attributes) |

### Meter Names

| Meter Name | Version | What It Covers |
|-----------|---------|----------------|
| `Whizbang.MessageTags` | 1.0.0 | Metric tag counters/histograms (from `[MetricTag]` attributes) |

### TracingOptions Reference

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Verbosity` | `TraceVerbosity` | `Off` | Global verbosity level |
| `Components` | `TraceComponents` | `None` | Which components emit traces (flags) |
| `EnableOpenTelemetry` | `bool` | `true` | Emit OTel spans via ActivitySource |
| `EnableStructuredLogging` | `bool` | `true` | Emit structured log messages |
| `EnableWorkerBatchSpans` | `bool` | `false` | Emit batch-level parent spans for workers |
| `EnablePerspectiveEventSpans` | `bool` | `false` | Emit per-event spans for perspectives |
| `TracedHandlers` | `Dictionary<string, TraceVerbosity>` | Empty | Handler-specific tracing overrides |
| `TracedMessages` | `Dictionary<string, TraceVerbosity>` | Empty | Message-specific tracing overrides |

### TraceVerbosity Levels

| Level | Value | What Is Traced |
|-------|-------|----------------|
| `Off` | 0 | Nothing |
| `Minimal` | 1 | Errors and explicitly marked traces only |
| `Normal` | 2 | + Lifecycle stage transitions |
| `Verbose` | 3 | + Handler discovery, outbox/inbox operations |
| `Debug` | 4 | + Full payload, timing breakdown, perspectives |

### TraceComponents Flags

| Component | Flag | What It Covers |
|-----------|------|----------------|
| `Handlers` | `1 << 0` | Handler invocations, completions, failures |
| `Lifecycle` | `1 << 1` | Lifecycle stage transitions |
| `Dispatcher` | `1 << 2` | Dispatcher operations, receptor discovery |
| `Messages` | `1 << 3` | Message dispatch and routing |
| `Events` | `1 << 4` | Event creation and publishing |
| `Outbox` | `1 << 5` | Outbox writes and delivery |
| `Inbox` | `1 << 6` | Inbox reads and processing |
| `EventStore` | `1 << 7` | Event store reads and writes |
| `Perspectives` | `1 << 8` | Perspective updates and queries |
| `Tags` | `1 << 9` | Tag hook processing |
| `Security` | `1 << 10` | Security context propagation |
| `Workers` | `1 << 11` | Background worker operations |
| `Errors` | `1 << 12` | Error and exception handling |

**Convenience combinations:**

| Combination | Components |
|-------------|-----------|
| `All` | Everything |
| `AllWithoutWorkers` | Everything except background worker noise |
| `Core` | Handlers + Dispatcher + Messages |
| `Messaging` | Messages + Events + Outbox + Inbox |
| `Storage` | EventStore + Perspectives |
| `Production` | Handlers + Errors + Security |

### Worker Configuration Reference

| Worker | Option | Default | Description |
|--------|--------|---------|-------------|
| **WorkCoordinatorPublisher** | `PollingIntervalMilliseconds` | `1000` | Poll frequency |
| | `LeaseSeconds` | `300` | Work item lease duration |
| | `StaleThresholdSeconds` | `600` | Stale instance threshold |
| | `PartitionCount` | `10000` | Hash partition count |
| | `IdleThresholdPolls` | `2` | Empty polls before idle |
| **PerspectiveWorker** | `PollingIntervalMilliseconds` | `1000` | Poll frequency |
| | `LeaseSeconds` | `300` | Perspective lease duration |
| | `StaleThresholdSeconds` | `600` | Stale instance threshold |
| | `PartitionCount` | `10000` | Hash partition count |
| | `IdleThresholdPolls` | `2` | Empty polls before idle |
| | `PerspectiveBatchSize` | `100` | Events per batch |

### Health Check Endpoints

| Endpoint | Type | What Passes |
|----------|------|-------------|
| `/health` | Readiness | All registered health checks |
| `/alive` | Liveness | Only checks tagged `"live"` |

### MessageFailureReason Values

| Value | Code | Description |
|-------|------|-------------|
| `None` | 0 | Success |
| `TransportNotReady` | 1 | Transport not connected |
| `TransportException` | 2 | Broker connectivity issue |
| `SerializationError` | 3 | JSON serialization failed |
| `ValidationError` | 4 | Message validation failed |
| `MaxAttemptsExceeded` | 5 | Retry limit exhausted |
| `LeaseExpired` | 6 | Buffer hold time exceeded |
| `EventStorageFailure` | 7 | Event store persistence failed |
| `Unknown` | 99 | Unclassified error |

### Logging Namespace Guidance

| Namespace | Recommended Level | Notes |
|-----------|-------------------|-------|
| `Default` | `Information` | Application logs |
| `Microsoft.AspNetCore` | `Warning` | Reduce HTTP request noise |
| `Microsoft.EntityFrameworkCore.Database.Command` | `Warning` | Logs slow/failed queries only |
| `Microsoft.EntityFrameworkCore.Database.Transaction` | `Warning` | Reduce transaction noise |
| `Microsoft.EntityFrameworkCore.Infrastructure` | `Warning` | Reduce EF infrastructure noise |
| `Whizbang.Core.Tracing` | `Debug` | Handler trace logs (set to `Information` for explicit traces only) |
| `Whizbang.Core.Workers` | `Information` | Worker lifecycle and health |
| `Whizbang.Observability` | `Information` | Observability hook processing |
