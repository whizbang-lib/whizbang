# Large Message Offloading (LMO) for Whizbang

## Context

Azure Service Bus (and most brokers) have message size limits (~256KB standard, ~100MB premium). Large event payloads (e.g., document snapshots, bulk data changes) can exceed these limits or degrade broker performance. This feature adds transparent message size measurement with configurable thresholds, and automatic offloading of large payloads to external blob storage — keeping the broker focused on ordering and lightweight envelope delivery.

The design must be zero-reflection, AOT-compatible, use the existing decorator/strategy patterns, and integrate with the hop metadata system for transparent send/receive.

---

## Architecture Overview

```
SEND SIDE (Outbox → Transport):
  OutboxWork → LargeMessagePublishStrategy (decorator) → TransportPublishStrategy → ITransport
                    │
                    ├─ < WarnAtBytes: pass through
                    ├─ ≥ WarnAtBytes: log + meter, pass through
                    ├─ ≥ OffloadAtBytes: upload to ILargeMessageStore, replace payload with sentinel, add hop metadata
                    └─ ≥ RejectAtBytes: return failure (MessageTooLarge)

RECEIVE SIDE (Transport → Inbox):
  IMessageEnvelope → IInboundEnvelopeInterceptor → _handleMessageAsync (existing)
                    │
                    └─ Check hop metadata "whizbang.lmo" → download from ILargeMessageStore → verify hash → restore payload
```

---

## Project Structure

```
src/
  Whizbang.LargeMessages/                           # Core LMO abstractions + send/receive logic
  Whizbang.LargeMessages.AzureBlobStorage/           # Azure Blob Storage implementation

tests/
  Whizbang.LargeMessages.Tests/                      # Unit tests
  Whizbang.LargeMessages.AzureBlobStorage.Tests/     # Unit tests for Azure impl
  Whizbang.LargeMessages.AzureBlobStorage.Integration.Tests/  # Integration tests w/ Azurite
```

NuGet packages: `SoftwareExtravaganza.Whizbang.LargeMessages`, `SoftwareExtravaganza.Whizbang.LargeMessages.AzureBlobStorage`

---

## Phase 1: Core Abstractions (`Whizbang.LargeMessages`)

### 1a. `ILargeMessageStore` interface

```csharp
public interface ILargeMessageStore {
    Task<LargeMessageReference> StoreAsync(
        Guid messageId, ReadOnlyMemory<byte> payload,
        LargeMessageStoreContext? context = null, CancellationToken ct = default);

    Task<ReadOnlyMemory<byte>> RetrieveAsync(
        LargeMessageReference reference, CancellationToken ct = default);

    Task DeleteAsync(LargeMessageReference reference, CancellationToken ct = default);
}
```

### 1b. `LargeMessageReference` (serializable as hop metadata)

```csharp
public sealed record LargeMessageReference {
    public required string Store { get; init; }       // "azure-blob"
    public required string Location { get; init; }    // container/key or URI
    public required string ContentHash { get; init; } // "sha256:<hex>"
    public string? Signature { get; init; }           // "hmac-sha256:<hex>" (optional)
    public required long OriginalSizeBytes { get; init; }
}
```

### 1c. `LargeMessageOptions`

```csharp
public sealed class LargeMessageOptions {
    public int WarnAtBytes { get; set; } = 200_000;
    public int OffloadAtBytes { get; set; } = 500_000;
    public int RejectAtBytes { get; set; } = 1_000_000;
    public bool EnableContentHashing { get; set; } = true;    // SHA-256
    public bool EnableSigning { get; set; } = false;          // HMAC-SHA-256
    public byte[]? SigningKey { get; set; }
    public StoreFailureAction OnStoreFailure { get; set; } = StoreFailureAction.Retry;
    public TimeSpan BlobTimeToLive { get; set; } = TimeSpan.FromDays(7);
    public bool PersistPayloadInBlobOnly { get; set; } = false;  // Skip SQL body storage
}

public enum StoreFailureAction { Retry, Reject, PassThrough }
```

### 1d. `InMemoryLargeMessageStore` — for unit testing

### 1e. `LargeMessagePayloadHelper` — static utilities for SHA-256 hash, HMAC-SHA-256 sign/verify (all AOT-safe via `System.Security.Cryptography`)

---

## Phase 2: Core Change — `IInboundEnvelopeInterceptor`

**Minimal change to `Whizbang.Core`** — add interface + 3-line integration:

### 2a. New interface in `Whizbang.Core/Transports/`:

```csharp
public interface IInboundEnvelopeInterceptor {
    Task<IMessageEnvelope> InterceptAsync(
        IMessageEnvelope envelope, CancellationToken ct = default);
}
```

### 2b. New `MessageFailureReason` enum values in `Whizbang.Core/Messaging/MessageFailureReason.cs`:

```csharp
MessageTooLarge = 8,
LargeMessageStoreUnavailable = 9,
LargeMessageIntegrityFailure = 10,
```

### 2c. Integration in `ServiceBusConsumerWorker._handleMessageAsync()` (line ~148):

```csharp
// After: await using var scope = _scopeFactory.CreateAsyncScope();
// Before: var strategy = scopedProvider.GetRequiredService<IWorkCoordinatorStrategy>();
var inboundInterceptor = scopedProvider.GetService<IInboundEnvelopeInterceptor>();
if (inboundInterceptor is not null) {
    envelope = await inboundInterceptor.InterceptAsync(envelope, ct);
}
```

This is ~3 lines. When LMO is not configured, `IInboundEnvelopeInterceptor` is not registered and GetService returns null — zero overhead.

**File**: `/Users/philcarbone/src/whizbang/whizbang/src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs`

---

## Phase 3: Send-Side — `LargeMessagePublishStrategy`

Decorator wrapping `IMessagePublishStrategy`. For each `OutboxWork`:

1. Estimate payload size: `work.Envelope.Payload.GetRawText().Length` (cheap for JsonElement; UTF-8 byte count ≈ char count for JSON)
2. **< WarnAtBytes**: pass through to inner strategy
3. **≥ WarnAtBytes, < OffloadAtBytes**: log warning, record `MessagesWarned` counter, pass through
4. **≥ OffloadAtBytes, < RejectAtBytes**:
   - Serialize `work.Envelope.Payload` (JsonElement) to UTF-8 bytes
   - Compute SHA-256 hash; optionally HMAC-SHA-256 signature
   - Upload via `ILargeMessageStore.StoreAsync()`
   - Create sentinel payload: `{"$whizbang_lmo": true}`
   - Add a new `MessageHop` with metadata key `"whizbang.lmo"` containing serialized `LargeMessageReference`
   - Replace `work.Envelope.Payload` with sentinel, add hop
   - If `PersistPayloadInBlobOnly` — also add metadata flag for inbox to skip SQL body
   - Delegate to inner strategy with modified work
5. **≥ RejectAtBytes**: return `MessagePublishResult { Success = false, Reason = MessageFailureReason.MessageTooLarge }`

For `PublishBatchAsync`: iterate items, apply same logic per-item, then delegate batch.

**Key files to modify**: None (new file). Decorator registered via DI.

---

## Phase 4: Receive-Side — `LargeMessageInboundInterceptor`

Implements `IInboundEnvelopeInterceptor`:

1. Check `envelope.GetMetadata("whizbang.lmo")` — if null, return envelope unchanged
2. Deserialize `LargeMessageReference` from the metadata JsonElement
3. Download payload bytes via `ILargeMessageStore.RetrieveAsync()`
4. Verify SHA-256 hash; if `EnableSigning`, verify HMAC signature → throw on mismatch
5. Deserialize bytes back to `JsonElement`
6. Replace envelope payload with restored JsonElement (create new `MessageEnvelope<JsonElement>` preserving all hops/metadata)
7. Return restored envelope — downstream code never knows offloading happened

---

## Phase 5: Configuration & DI Registration

Builder extension on `WhizbangBuilder`:

```csharp
builder.WithLargeMessageHandling(opts => {
    opts.WarnAtBytes = 200_000;
    opts.OffloadAtBytes = 500_000;
    opts.RejectAtBytes = 1_000_000;
    opts.EnableSigning = true;
    opts.SigningKey = Convert.FromBase64String("...");
});
```

Registers:
- `LargeMessageOptions` as singleton
- Decorates `IMessagePublishStrategy` with `LargeMessagePublishStrategy`
- Registers `IInboundEnvelopeInterceptor` → `LargeMessageInboundInterceptor`
- Registers `LargeMessageMetrics` singleton

**File**: `Whizbang.LargeMessages/LargeMessageBuilderExtensions.cs`

---

## Phase 6: Azure Blob Storage Implementation (`Whizbang.LargeMessages.AzureBlobStorage`)

### 6a. `AzureBlobLargeMessageStore` implementing `ILargeMessageStore`

- Uses `Azure.Storage.Blobs` SDK (`BlobContainerClient`)
- Blob naming: `{messageId}/{Guid}` (unique per offload)
- Sets blob metadata: `OriginalSizeBytes`, `ContentHash`, `TTL`
- Supports managed identity and connection string auth

### 6b. Registration extension:

```csharp
opts.UseAzureBlobStorage(connectionString, containerName);
// or
opts.UseAzureBlobStorage(new Uri(blobEndpoint), new DefaultAzureCredential());
```

### 6c. Dependencies: `Azure.Storage.Blobs`, `Azure.Identity` (optional)

---

## Phase 7: Observability

### Meter: `Whizbang.LargeMessages` v1.0.0

| Instrument | Type | Description |
|---|---|---|
| `whizbang.lmo.messages_offloaded` | Counter | Messages offloaded to blob |
| `whizbang.lmo.messages_restored` | Counter | Messages restored from blob |
| `whizbang.lmo.messages_warned` | Counter | Messages exceeding warn threshold |
| `whizbang.lmo.messages_rejected` | Counter | Messages exceeding reject threshold |
| `whizbang.lmo.store_failures` | Counter | Blob store operation failures |
| `whizbang.lmo.blob_upload_duration_ms` | Histogram | Upload latency |
| `whizbang.lmo.blob_download_duration_ms` | Histogram | Download latency |
| `whizbang.lmo.original_message_size_bytes` | Histogram | Pre-offload message sizes |
| `whizbang.lmo.integrity_failures` | Counter | Hash/signature verification failures |

### ActivitySource: `Whizbang.LargeMessages` v1.0.0

- Span: `LMO Upload` (Producer) — around blob store
- Span: `LMO Download` (Consumer) — around blob retrieve + verify

---

## Phase 8: Testing

### Unit Tests (`Whizbang.LargeMessages.Tests/`)

- `LargeMessagePublishStrategyTests` — all threshold behaviors, batch handling, sentinel replacement, hop metadata
- `LargeMessageInboundInterceptorTests` — metadata detection, payload restore, hash verification, signature verification, integrity failure
- `LargeMessagePayloadHelperTests` — hash computation, signing, size estimation
- `InMemoryLargeMessageStoreTests` — store/retrieve/delete
- `LargeMessageOptionsTests` — validation, defaults
- `LargeMessageMetricsTests` — counter increments

### Unit Tests (`Whizbang.LargeMessages.AzureBlobStorage.Tests/`)

- `AzureBlobLargeMessageStoreTests` — mocked BlobContainerClient

### Integration Tests (`Whizbang.LargeMessages.AzureBlobStorage.Integration.Tests/`)

- **Azurite container** via Aspire AppHost (or direct Docker fixture matching existing patterns)
- End-to-end: large payload → offload → transport (InProcess) → receive → restore → verify identical
- Blob lifecycle: store, retrieve, delete, exists
- Hash verification failure detection (tampered blob)
- Store unavailability handling

---

## Phase 9: Documentation

### New doc page: `src/assets/docs/drafts/large-message-offloading.md`

Contents:
- Overview & motivation (broker size limits)
- Architecture diagram (send/receive flow)
- Configuration guide with code examples
- Threshold tuning guidance
- Azure Blob Storage setup
- Security (hashing, signing)
- Observability (meters, traces)
- Blob cleanup strategies (TTL, explicit)
- Troubleshooting

### Code annotations:
- `<docs>messaging/large-message-offloading</docs>` on all public APIs
- `<tests>` tags linking to test files

---

## Critical Files to Modify

| File | Change |
|---|---|
| `src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs` | +3 lines: resolve & call `IInboundEnvelopeInterceptor` |
| `src/Whizbang.Core/Messaging/MessageFailureReason.cs` | +3 enum values |
| `src/Whizbang.Core/Transports/` (new file) | `IInboundEnvelopeInterceptor.cs` interface |
| `whizbang.sln` | Add new projects |

All other code is **new files** in new projects.

---

## Key Design Decisions

1. **Decorator on `IMessagePublishStrategy`** (not `ITransport`) — intercepts at the right level with access to `OutboxWork` context, follows existing decorator patterns
2. **Hop metadata** (`whizbang.lmo` key) for blob reference — travels naturally through existing serialization, requires zero envelope schema changes
3. **`IInboundEnvelopeInterceptor` in Core** — minimal 1-interface addition; optional via DI (null when LMO not configured)
4. **Separate packages** — Core stays thin; blob storage is opt-in
5. **Size estimation via `GetRawText().Length`** — avoids full serialization for the common case (under threshold); only full byte[] serialization when offloading
6. **SHA-256 + optional HMAC-SHA-256** — `System.Security.Cryptography` is fully AOT-compatible
7. **TTL-based cleanup** for v1 — Azure Blob lifecycle policies handle blob expiry; explicit cleanup deferred to v2

---

## Verification

1. **Unit tests**: `dotnet test` on `Whizbang.LargeMessages.Tests` and `Whizbang.LargeMessages.AzureBlobStorage.Tests`
2. **Integration tests**: `dotnet test` on `Whizbang.LargeMessages.AzureBlobStorage.Integration.Tests` (requires Docker for Azurite)
3. **End-to-end manual**: Configure sample ECommerce app with LMO, send message exceeding offload threshold, verify:
   - Blob appears in Azurite
   - Transport message is small (sentinel only)
   - Receiving service transparently restores full payload
   - OTel metrics show offload/restore counters
4. **`dotnet format`** on all new projects
5. **AOT analysis**: Verify no IL2026/IL2046/IL2075 warnings in new projects
