<p align="center">
  <img alt="Whizbang — One Runtime. Any Store. Every Message." src="assets/hero-banner.svg" width="100%">
</p>

<p align="center">
  <a href="https://whizba.ng/">Documentation</a> &middot;
  <a href="https://www.nuget.org/packages/SoftwareExtravaganza.Whizbang.Core/">NuGet</a> &middot;
  <a href="https://github.com/whizbang-lib/whizbang/releases">Releases</a> &middot;
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://codecov.io/gh/whizbang-lib/whizbang"><img src="https://codecov.io/gh/whizbang-lib/whizbang/branch/main/graph/badge.svg" alt="codecov"></a>
  <a href="https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang"><img src="https://sonarcloud.io/api/project_badges/measure?project=whizbang-lib_whizbang&metric=alert_status" alt="Quality Gate Status"></a>
  <a href="https://www.nuget.org/packages/SoftwareExtravaganza.Whizbang.Core/"><img src="https://img.shields.io/nuget/v/SoftwareExtravaganza.Whizbang.Core.svg" alt="NuGet"></a>
  <a href="https://www.nuget.org/packages/SoftwareExtravaganza.Whizbang.Core/"><img src="https://img.shields.io/nuget/vpre/SoftwareExtravaganza.Whizbang.Core.svg?label=nuget%20pre-release" alt="NuGet pre-release"></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT"></a>
</p>

<p align="center">
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/security-secrets.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/security-secrets.yml/badge.svg" alt="Secret Scanning"></a>
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml/badge.svg" alt="Supply Chain"></a>
  <a href="https://securityscorecards.dev/viewer/?uri=github.com/whizbang-lib/whizbang"><img src="https://api.securityscorecards.dev/projects/github.com/whizbang-lib/whizbang/badge" alt="OSSF Scorecard"></a>
</p>

<p align="center">
  <a href="https://codecov.io/gh/whizbang-lib/whizbang"><img src="https://codecov.io/gh/whizbang-lib/whizbang/graphs/tree.svg?token=F1AZXLI2MM" alt="Codecov coverage grid" width="200"></a>
</p>

---

<h3 align="center"><a href="https://whizba.ng">Read the full documentation at whizba.ng</a></h3>

---

Whizbang is a .NET library for event-driven, CQRS and event-sourced applications. Handlers, routing, read
models and storage are wired by source generators at compile time: no reflection, Native AOT from day one.

> **Status:** pre-1.0. Stable releases ship as `0.x.0` on nuget.org, with alpha builds published from
> `develop` in between. Package IDs carry the `SoftwareExtravaganza.` prefix
> (`SoftwareExtravaganza.Whizbang.Core`, and so on).

## At a glance

```csharp
// A command, and the event it produces. [StreamId] names the stream the event belongs to.
public record CreateOrder([property: StreamId] Guid OrderId, string ProductName, int Quantity) : ICommand;
public record OrderCreated([property: StreamId] Guid OrderId, string ProductName, int Quantity) : IEvent;

// A receptor: a stateless handler. The event it returns is stored and published for you.
public class CreateOrderReceptor : IReceptor<CreateOrder, OrderCreated> {
  public ValueTask<OrderCreated> HandleAsync(CreateOrder message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new OrderCreated(message.OrderId, message.ProductName, message.Quantity));
}

// Dispatch it, typed end to end.
var created = await dispatcher.LocalInvokeAsync<CreateOrder, OrderCreated>(
  new CreateOrder(TrackedGuid.New(), "Coffee", 2));
```

The [Quick Start](https://whizba.ng/docs/getting-started/quick-start) takes this to a running service with
PostgreSQL, a read model and a query.

## What's in the box

- **Receptors and the dispatcher:** type-safe handlers, local and remote dispatch, correlation and causation
  on every message.
- **Event store and outbox/inbox:** append-only streams with UUIDv7 ordering, exactly-once handling through
  the inbox, reliable publishing through the outbox, dead-letter recovery.
- **Perspectives and lenses:** read models built from events, queried through LINQ that translates to SQL,
  with physical columns and search indexes where you declare them.
- **Sagas:** multi-stream coordination with per-item progress and recovery.
- **Priorities, payload limits and offloads:** busy services keep interactive work first, oversized
  messages are refused at the sender, and large bodies can move to blob storage.

| Area | Packages |
|---|---|
| Core | `Whizbang.Core`, `Whizbang.Generators`, `Whizbang.Observability` |
| Storage | `Whizbang.Data.EFCore.Postgres` (+ `.Generators`), `Whizbang.Data.Dapper.Postgres`, `Whizbang.Data.Dapper.Sqlite`, `Whizbang.Data.Postgres`, `Whizbang.Data.Schema`, and the `.Custom` bases for your own store |
| Transports | `Whizbang.Transports.RabbitMQ`, `Whizbang.Transports.AzureServiceBus`, in-process |
| API surfaces | `Whizbang.Transports.HotChocolate` (GraphQL), `Whizbang.Transports.FastEndpoints` (REST), `Whizbang.Transports.Mutations`, `Whizbang.SignalR` |
| Hosting | `Whizbang.Hosting.AspNet`, `Whizbang.Hosting.RabbitMQ` and `Whizbang.Hosting.Azure.ServiceBus` (Aspire) |
| Sagas | `Whizbang.Sagas`, `Whizbang.Sagas.Contracts`, `Whizbang.Sagas.Generators` |
| Offloads | `Whizbang.Offloads.AzureBlob`, `Whizbang.Offloads.InMemory` |
| Tools | `Whizbang.CLI`, `Whizbang.Migrate` (from Marten/Wolverine), `Whizbang.LanguageServer` |

Every package is published as `SoftwareExtravaganza.<name>`.

## Requirements

- **.NET 10**
- **PostgreSQL** for the production stores (tested against PostgreSQL 17). Search fields use the `pg_trgm`
  extension, and the framework creates it when the server allows.
- RabbitMQ or Azure Service Bus when services talk across processes; neither is needed in-process.

## Getting started

```bash
dotnet add package SoftwareExtravaganza.Whizbang.Core
dotnet add package SoftwareExtravaganza.Whizbang.Data.EFCore.Postgres
```

- [Quick Start](https://whizba.ng/docs/getting-started/quick-start)
- [Samples](samples/): an e-commerce system with services on RabbitMQ and Azure Service Bus, run with Aspire.

## Quality

<!-- auto:quality -->
- **Tests:** 26,000+ across unit, generator, integration and transport suites, run on every pull request.
- **Coverage:** 100% of library lines; every pull request must cover all of its new lines and add no
  SonarCloud findings.
<!-- /auto:quality -->

Dispatch is designed to stay allocation-free on the in-process path; the benchmarks live in
[`benchmarks/`](benchmarks/).

## Contributing and support

- [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test and send changes.
- [SECURITY.md](SECURITY.md) to report a vulnerability privately.
- [Issues](https://github.com/whizbang-lib/whizbang/issues) for bugs and questions.

## License

[MIT](LICENSE)
