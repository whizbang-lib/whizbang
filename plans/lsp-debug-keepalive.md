# LSP Debug Keepalive — Implementation Plan

## Problem

When a developer hits a breakpoint, the CLR freezes ALL managed threads. The app's `WorkCoordinatorPublisherWorker` stops polling, heartbeats stop, leases expire, and after 5-10 minutes other instances steal the paused app's work — causing duplicate message processing.

## Solution: Load Developer's DLLs into the LSP

The LSP loads the developer's compiled Whizbang assemblies into its own process via `AssemblyLoadContext` and runs a **mini keepalive host** using the library's own APIs. This uses the same version of Whizbang the developer is using — no reimplementation, no raw SQL, no version mismatch.

```
Developer's App (FROZEN at breakpoint)
  │ ← All threads frozen
  │
Whizbang LSP Process (RUNNING independently)
  │
  ├─ Loads developer's bin/Debug/net10.0/*.dll
  ├─ Calls AddWhizbang() + AddRabbitMQTransport() etc.
  ├─ Builds PARTIAL service provider:
  │   ✅ WorkCoordinatorPublisherWorker (heartbeat + lease renewal)
  │   ✅ ITransport (RabbitMQ/ServiceBus connection keepalive)
  │   ✅ IServiceInstanceProvider (instance identity)
  │   ❌ TransportConsumerWorker (NO message processing)
  │   ❌ PerspectiveWorker (NO perspective processing)
  │   ❌ Receptors/Handlers (NO business logic)
  │
  └─ Runs keepalive loop until debug resumes
```

## Timeout Reference

| Resource | Timeout | What Happens | LSP Handles? |
|----------|---------|-------------|:---:|
| Work coordinator leases (wh_outbox/wh_inbox) | 300s (5 min) | Other instances steal work | YES — WorkCoordinatorPublisherWorker renews |
| Instance heartbeat (wh_service_instances) | 600s (10 min) | Instance deleted, work released | YES — Worker updates heartbeat |
| DB command timeout | 5s | Query cancelled, retried | YES — Worker polls normally |
| RabbitMQ connection heartbeat | Broker default | Connection drops, auto-recovery | YES — Transport in LSP keeps connection |
| Azure SB message locks | 60s | Messages redeliver | PARTIAL — Transport helps but lock tokens are in frozen app |
| Perspective sync (DebugAwareClock) | 5s | Already debug-aware | YES — ExternalHook mode |
| Stream locks | 30s + keepalive | Lock expires, another instance takes over | YES — Keepalive runs in LSP |

## New Library: `Whizbang.Debugging`

### Location: `src/Whizbang.Debugging/`

```
src/Whizbang.Debugging/
├── Whizbang.Debugging.csproj
├── DebugHost.cs                    # Main: loads DLLs, builds partial host, runs keepalive
├── DebugHostOptions.cs             # Configuration
├── DebugHostBuilder.cs             # Fluent builder
├── AssemblyLoader.cs               # Isolated AssemblyLoadContext management
├── ServiceFilteringExtensions.cs   # Remove consumer/handler registrations from IServiceCollection
└── IDebugHostCallback.cs           # Callbacks for LSP (status, errors)
```

### Core API

```csharp
var host = new DebugHostBuilder()
    .WithAssemblyPath("/path/to/Developer.Service/bin/Debug/net10.0/")
    .WithStartupAssembly("Developer.Service.dll")
    .WithConnectionStringOverride("Host=localhost;Database=dev_db;...")
    .OnHeartbeat(info => logger.Log("Heartbeat: {Id}", info.InstanceId))
    .OnLeaseRenewed(info => logger.Log("Renewed {N} leases", info.Count))
    .OnError(ex => logger.LogError(ex, "Keepalive error"))
    .Build();

await host.StartAsync(ct);
// ... debug session active, keepalive running ...
await host.StopAsync(ct);
```

### Assembly Loading

1. Find output directory — scan workspace for `*.csproj`, read `<OutputPath>`, find `bin/Debug/net10.0/`
2. Load via `AssemblyLoadContext` (isolated, collectible for unload)
3. Find host builder — scan for `Program.cs` / `IHostBuilder` configuration
4. Extract DI — call developer's `ConfigureServices` to populate `IServiceCollection`
5. Filter services — remove `TransportConsumerWorker`, `PerspectiveWorker`, all `IReceptor<>` registrations
6. Keep only — `WorkCoordinatorPublisherWorker`, `ITransport`, `IServiceInstanceProvider`, `IWorkCoordinator`
7. Build and run

### Service Filtering

```csharp
public static IServiceCollection RemoveMessageProcessing(this IServiceCollection services) {
    services.RemoveAll<TransportConsumerWorker>();
    services.RemoveAll<PerspectiveWorker>();
    services.RemoveAll(d => d.ServiceType.IsGenericType &&
        d.ServiceType.GetGenericTypeDefinition() == typeof(IReceptor<>));
    services.RemoveAll(d => d.ServiceType.IsGenericType &&
        d.ServiceType.GetGenericTypeDefinition() == typeof(IReceptor<,>));
    services.RemoveAll<IPerspectiveRunnerRegistry>();
    return services;
}
```

## Implementation Phases

### Phase A: `Whizbang.Debugging` Library (TDD)

| Class | Tests | Purpose |
|-------|-------|---------|
| `AssemblyLoader` | Load, isolated context, collectible unload, missing DLL | Safe assembly loading |
| `ServiceFilteringExtensions` | Remove consumers, remove receptors, keep transport, keep heartbeat | DI filtering |
| `DebugHostBuilder` | Fluent API, validation, build with/without overrides | Host construction |
| `DebugHost` | Start/stop lifecycle, heartbeat callback, lease callback, error, dispose | Host management |

### Phase B: LSP Integration

| File | Change |
|------|--------|
| `Whizbang.LanguageServer.csproj` | Reference `Whizbang.Debugging` |
| `Debugging/DebugKeepAliveService.cs` | New: manages DebugHost lifecycle |
| `Handlers/DebugSessionHandler.cs` | Update: start host on pause, stop on resume |
| `Protocol/CustomParams.cs` | Add processId + projectPath to pause params |

### Phase C: TypeScript Client

| File | Change |
|------|--------|
| `extension.ts` | Send processId + projectPath in debug notifications |
| `package.json` | Add keepalive settings (enabled, outputPath) |

### Phase D: DebugAwareClock Enhancement

| File | Change |
|------|--------|
| `DebuggerAwareClock.cs` | Add `SignalExternalPauseState(bool)` for ExternalHook mode |
| `DebuggerAwareClockExternalHookTests.cs` | New tests |

## Verification

1. Start Whizbang sample app with RabbitMQ
2. Attach debugger, hit breakpoint
3. Verify LSP output: "Debug keepalive active, loaded Developer.Service.dll"
4. Wait 6+ minutes (past lease timeout)
5. Query `wh_service_instances` → `last_heartbeat_at` keeps updating
6. Query `wh_outbox` → `lease_expiry` stays in the future
7. Other instances do NOT steal work
8. Resume → normal processing continues, no duplicate messages

## Trade-offs

- **RabbitMQ**: Transport in LSP keeps its OWN connection alive. The frozen app's connection will drop and auto-recover on resume. Messages in the app's prefetch buffer are requeued. Idempotent inbox dedup prevents double-processing.
- **Azure SB**: Same as RabbitMQ for connection. Message locks tied to frozen app will expire and redeliver. Idempotent dedup handles this.
- **Very long breakpoints (>1 hour)**: Keepalive loop runs indefinitely — leases and heartbeats stay fresh.
- **DLL version mismatch**: Isolated AssemblyLoadContext loads the developer's exact DLLs, so version always matches.
