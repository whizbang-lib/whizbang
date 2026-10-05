# Roadmap

What the Whizbang project intends to do, and not do, over the next twelve months (October 2026 to
October 2027). It is reviewed when a release changes the picture and at least every quarter; the
[governance model](GOVERNANCE.md) says who decides. Ideas and questions are welcome in
[Discussions](https://github.com/whizbang-lib/whizbang/discussions).

## Toward 1.0

Whizbang is pre-1.0: stable releases ship as `0.x.0`. Version 1.0 is the point at which the public API
carries a stability guarantee. It ships when the following are done:

- **The full data-retention story.** Ephemeral events that are never stored, destruction and time-to-live
  for data that must expire, a temporal engine for history queries, archival, stream compaction that
  carries the current state forward, and crypto-shredding so a person's data can be made unreadable on
  request.
- **A stable surface that includes sagas, composite events and collective events**, with their
  documentation and hardening finished, not shipped as experimental.
- **Consistent receive behavior across the supported brokers**, so a message no service handles is
  treated the same way on every transport.
- **Reference documentation for every package**: every public API documented, linked to its page on the
  docs site and to the tests that pin it.
- **Reliability work found in production-scale testing**, tracked as issues and fixed before 1.0.

## After 1.0

- **Throughput and fairness**: higher message throughput under load, and priority tiers so urgent work
  is not stuck behind bulk work.
- **Database load**: separating read and write connections, and index and query improvements for the
  framework's own tables.
- **Tooling**: richer debugging support in the IDE extension, and generated dependency-injection
  manifests.

## Not planned for this period

- **Brokers other than Azure Service Bus and RabbitMQ.** Kafka and Event Hubs adapters were removed from
  the runtime and are not coming back in this period.
- **Production stores other than PostgreSQL.** SQLite stays available for development and tests only.
- **Runtime reflection.** Wiring stays source-generated and Native AOT compatible; features that would
  need reflection at runtime are not taken on.
- **Older .NET versions.** The library targets the current long-term-support .NET (.NET 10).
- **A hosted or managed service.** Whizbang is a library that runs inside your own applications.

## Where the detail lives

- [Releases](https://github.com/whizbang-lib/whizbang/releases): what has shipped, release by release.
- [Issues](https://github.com/whizbang-lib/whizbang/issues): individual bugs and planned changes.
- [CHANGELOG.md](CHANGELOG.md): notable changes.
