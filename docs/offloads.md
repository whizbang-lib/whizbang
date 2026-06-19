---
title: Message body offload (claim-check)
description: Offload large message bodies to external storage and carry a small claim ticket on the wire.
related_files:
  - src/Whizbang.Core/Offloads/MessageBodyOffloadOptions.cs
  - src/Whizbang.Core/Offloads/OffloadServiceCollectionExtensions.cs
  - src/Whizbang.Core/Offloads/BodyOffloadPostSerializeHook.cs
  - src/Whizbang.Core/Offloads/BodyClaimRehydrator.cs
  - src/Whizbang.Offloads.AzureBlob/AzureBlobOffloadServiceCollectionExtensions.cs
  - src/Whizbang.Offloads.AzureBlob/AzureBlobOffloadOptions.cs
  - src/Whizbang.Offloads.InMemory/InMemoryOffloadServiceCollectionExtensions.cs
related_tests:
  - tests/Whizbang.Offloads.AzureBlob.Tests/AzureBlobOffloadFromConfigurationTests.cs
  - tests/Whizbang.Offloads.AzureBlob.Tests/AzureBlobOffloadDIRegistrationTests.cs
  - tests/Whizbang.Core.Tests/Offloads/BodyOffloadPostSerializeHookTests.cs
  - tests/Whizbang.Core.Tests/Offloads/BodyClaimRehydratorTests.cs
last_verified: 2026-06-19
---

# Message body offload (claim-check)

Brokers cap message size (Azure Service Bus Standard ~256 KB, Premium ~100 MB; RabbitMQ
practically smaller under load). When a serialized message body exceeds a configurable
threshold, Whizbang uploads the body to an external **body store** and replaces it on the wire
with a small **claim ticket** (`MessageBodyClaim`: provider name, key, size, content hash). The
receiver rehydrates the body from the store before handling. This is the classic *claim-check*
pattern — the broker only ever moves ordering + lightweight envelope metadata.

Offload is **opt-in by configuration**: a service publishes inline until a provider is configured,
then large bodies offload automatically. Nothing in the message contract changes.

## Quick start (config-driven — recommended)

One call wires everything from configuration. Put it next to your other Whizbang registrations
(it is a plain `IServiceCollection` extension, independent of the `AddWhizbang(...)` chain):

```csharp
using Whizbang.Offloads.AzureBlob;

services.AddWhizbangAzureBlobOffloadsFromConfiguration(configuration);
```

It scans every provider under `Whizbang:Offloads:AzureBlob:<name>`, registers a blob store for each,
enables the send-side hook, and binds the selector from `Whizbang:BodyOffload`. With no providers
configured it is a **no-op** — so the same code is safe in every environment, and a deployment turns
offload on simply by providing the env vars.

### Configuration

`appsettings.json` form:

```json
{
  "Whizbang": {
    "Offloads": {
      "AzureBlob": {
        "jdx-offload": {
          "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=…;AccountKey=…;EndpointSuffix=core.windows.net",
          "ContainerName": "whizbang-offload-bodies",
          "DefaultAccessTier": "Cool",
          "MaxDownloadBytes": 104857600
        }
      }
    },
    "BodyOffload": {
      "ProviderName": "jdx-offload",
      "SizeThresholdBytes": 65536,
      "ActiveCleanup": false
    }
  }
}
```

Env-var form (what you paste into Helm / Kubernetes — `:` becomes `__`):

```bash
Whizbang__Offloads__AzureBlob__jdx-offload__ConnectionString="…"
Whizbang__Offloads__AzureBlob__jdx-offload__ContainerName="whizbang-offload-bodies"
Whizbang__Offloads__AzureBlob__jdx-offload__DefaultAccessTier="Cool"
Whizbang__Offloads__AzureBlob__jdx-offload__MaxDownloadBytes="104857600"
Whizbang__BodyOffload__ProviderName="jdx-offload"      # MUST match a provider name above
Whizbang__BodyOffload__SizeThresholdBytes="65536"
Whizbang__BodyOffload__ActiveCleanup="false"
```

The provider name (`jdx-offload`) is arbitrary; it just ties the provider registration to the
selector. `Whizbang:BodyOffload:ProviderName` is what actually **turns offload on** — without it the
hook is a registered no-op (inline publish).

## Options reference

**Provider — `AzureBlobOffloadOptions`** (`Whizbang:Offloads:AzureBlob:<name>`):

| Key | Default | Notes |
|---|---|---|
| `ConnectionString` | — (required) | Account key / SAS / `UseDevelopmentStorage=true` (Azurite). |
| `ContainerName` | `whizbang-offload-bodies` | Created on first upload if absent. Give each slot its own container when slots share an account. |
| `DefaultAccessTier` | account default | `Hot` \| `Cool` \| `Cold` \| `Archive`. **Do not** use `Archive` — it needs an out-of-band rehydrate before download. |
| `MaxDownloadBytes` | null (uncapped) | Defensive cap; refuses to download a claim reporting a larger body. |

**Selector — `MessageBodyOffloadOptions`** (`Whizbang:BodyOffload`):

| Key | Default | Notes |
|---|---|---|
| `ProviderName` | null | The active provider. `null` ⇒ offload disabled (publish inline). |
| `SizeThresholdBytes` | 65536 (64 KB) | Bodies at/above this offload. Set below the transport ceiling to leave envelope headroom. |
| `ActiveCleanup` | false | `false` ⇒ rely on a blob lifecycle rule to delete old bodies (recommended). `true` ⇒ Whizbang deletes after the inbox row is acked. |

## Manual / advanced wiring

For a single provider or non-config scenarios, the building blocks the convention method composes
are public:

```csharp
services.AddWhizbangAzureBlobOffload("jdx-offload", opts => {
    opts.ConnectionString = connectionString;
    opts.ContainerName = "whizbang-offload-bodies";
});
services.AddWhizbangBodyOffload();                 // registers the claim-check post-serialize hook
services.Configure<MessageBodyOffloadOptions>(o => o.ProviderName = "jdx-offload");
```

The in-memory provider (`AddWhizbangInMemoryOffload(name)`, package
`Whizbang.Offloads.InMemory`) is for tests/fixtures — bodies live in process memory.

> **Note:** binding `AzureBlobOffloadOptions` from configuration must be done explicitly (as the
> convention method does), not via `configuration.Bind(opts)` — `DefaultAccessTier` is an Azure SDK
> extensible-enum struct that `ConfigurationBinder` cannot convert from a string.

## How it works

- **Send** — `BodyOffloadPostSerializeHook` runs in the publish pipeline (`TransportPublishStrategy`
  consults a `PostSerializeHookChain`). When the serialized body ≥ `SizeThresholdBytes`, it uploads
  to the selected `IMessageBodyStore` and swaps the wire payload for a `MessageBodyClaim`.
- **Receive** — both the RabbitMQ and Azure Service Bus transports resolve the optional
  `PostSerializeHookChain` and (on the inbound path) `BodyClaimRehydrator` downloads the body from
  the store named in the claim, verifies its hash, and restores the payload before handling.
- **Receiver requirement** — a receiving service must register the **same provider name** so the
  rehydrator can find the store. The config-driven call on every service satisfies this uniformly.

## Verifying it took

- Metrics: `whizbang.transport.body_offload.*` (send) and `whizbang.transport.body_claim.rehydrated.*`
  (receive) appear once offload fires.
- A wrong/empty connection string surfaces as
  `InvalidOperationException: AzureBlobOffloadOptions.ConnectionString is required for provider '<name>'`
  the first time the store is resolved (i.e. on the first offload).
- A receiver missing the provider surfaces as a clear "no `IMessageBodyStore` registered under
  provider name '<name>'" error from `BodyClaimRehydrator`.
