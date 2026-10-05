# Security assurance case

Why Whizbang's security requirements are met: what the library protects, against whom, where the trust
boundaries are, which design principles it applies, and how it counters the common implementation
weaknesses. The last section says plainly what an application can and cannot expect from the library.

Whizbang is a library, not a service. It runs inside the applications that use it, with their
privileges, and talks to infrastructure the application owns: a PostgreSQL database, an Azure Service
Bus or RabbitMQ broker, and optionally a blob store for large message bodies. Its security therefore
depends on, and is bounded by, how the application configures that infrastructure.

## Security requirements

1. Messages and events are stored and delivered without being altered or lost silently, and a redelivered
   message is not applied twice.
2. Data is visible only within the scope (for example the tenant) it belongs to, when the application
   uses the framework's scoping.
3. Untrusted input cannot make the framework run arbitrary code, execute arbitrary SQL, or instantiate
   types the application did not register.
4. Message bodies the application chooses to encrypt stay confidential and tamper-evident at rest.
5. The packages users install are the ones this repository built and tested.

## Threat model

| Actor | Can | Wants to |
|---|---|---|
| A sender on the broker that is not one of the application's services (misconfigured, compromised or hostile) | Put arbitrary messages on a queue or topic the application reads | Crash consumers, exhaust their memory, read or change another tenant's data, run code |
| A user of the application's HTTP surface (REST, GraphQL, SignalR built with the framework's transports) | Send arbitrary requests | Read or change data outside their scope, overload the service |
| Someone with access to the blob store holding offloaded bodies | Read or replace stored bodies | Read message content, or substitute a body |
| A supplier in the build chain | Publish a malicious version of a dependency or a build action | Get code into the packages users install |
| Someone who intercepts or replaces a download | Serve a modified package | Get users to run code they did not intend |

Out of scope: an attacker who already controls an application host, its configuration or its database
credentials. Whizbang runs with the application's privileges and cannot defend against its own host.

## Trust boundaries

1. **Broker to application.** Every received message is untrusted input, including its envelope (type
   name, headers, security context).
2. **HTTP client to application.** Requests arrive through ASP.NET Core; authentication and
   authorization are the application's, using ASP.NET Core's mechanisms, and the framework's transports
   filter what they read through the scoped lenses to the caller's scope.
3. **Application to database.** The database is trusted with the data; what crosses into SQL must not
   change the statement's meaning.
4. **Application to blob store.** Offloaded bodies leave the application and come back; they are checked
   before use.
5. **Repository to user.** Source becomes packages on nuget.org through CI; the user must be able to
   verify what they install.

## Secure design principles applied

- **Least privilege.** Every CI workflow starts with read-only repository access and grants write access
  only to the job that publishes or pushes. At runtime the library needs no privileges beyond the application's own, and it changes the database schema only with the credentials the application gives it.
- **Fail-safe defaults.** A message whose type the receiving service has no metadata for is never
  deserialized. An offloaded body whose hash does not match is not used. A sealed body opens only under
  the cipher name and key identifier it was sealed with.
- **Complete mediation.** Scoped reads go through the scoped lens factory, which applies the caller's
  scope to every query rather than relying on each query to remember it.
- **Economy of mechanism.** Wiring is generated at compile time, so the runtime has no reflection-driven
  discovery to subvert, and Native AOT compatibility keeps it that way.
- **Open design.** The code, the security process and this document are public; nothing relies on the
  design being secret.
- **Separation of privilege.** Data keys and key-encryption keys are separate: each body gets a fresh
  data key, and only its wrapped form travels.

## Common weaknesses countered

| Weakness | How it is countered |
|---|---|
| SQL injection (CWE-89) | Values always travel as SQL parameters (Npgsql, Dapper and EF Core). Identifiers come from the application's configuration and generated code, not from messages, and the one store path that takes column names at runtime checks them against an identifier pattern. |
| Deserialization of untrusted data (CWE-502) | Payloads deserialize only through source-generated JSON metadata that the application and the framework register; no reflection-based resolver is configured. A type name with no registered metadata is never deserialized: it goes to a raw receptor if one is registered, and is otherwise dropped. |
| Uncontrolled resource consumption (CWE-400) | A maximum payload size (5 MiB by default, configurable per message type and per dispatch) is enforced where a message is serialized, before it is stored or sent. Claim batches and work queues are bounded. |
| Improper integrity checking (CWE-354) | Offloaded bodies are verified against the SHA-256 hash in their claim before they are used; encrypted bodies also carry an authentication tag. |
| Broken or risky cryptography (CWE-327) | Security relies only on AES-256-GCM from the .NET runtime with a fresh random key and nonce per body. MD5 and SHA-1 appear only to derive stable identifiers, never for protection. |
| Exposure of sensitive information (CWE-200) | Field-level permissions can mask or redact fields for callers without access. Secret scanning runs on every push and pull request. |
| Use of vulnerable components (CWE-1395) | Dependencies are pinned with committed lock files and restored in locked mode; Dependabot and a weekly OSV scan watch for advisories. |
| Unverified downloads (CWE-494) | Stable releases carry a Sigstore-signed SLSA provenance statement; nuget.org adds its repository signature. See [Verifying a release](../SECURITY.md#verifying-a-release). |

Every change also passes static analysis (CodeQL, SonarCloud, Roslyn analyzers with warnings as errors),
the full test suite with 100% coverage of new lines, and property-based tests on the identifier and
serialization invariants.

## What you can and cannot expect

**You can expect** the library to:

- deduplicate redelivered messages for each handler, apply a stream's events to read models in order, and
  retry a failed message and then dead-letter it rather than lose it (a message no service handles is
  the exception: it is dropped by design);
- refuse to deserialize types you did not register, and refuse oversized payloads from senders that use
  the framework;
- keep scoped data within its scope when you read it through the scoped lenses and transports;
- detect a tampered offloaded body, and keep encrypted bodies confidential when you register a cipher.

**You cannot expect** the library to:

- **authenticate senders.** The security context carried on a message is not signed: anyone who can
  publish to your broker can claim any context. Restrict who can send to your queues and topics, and
  use the broker's own authentication and network controls.
- **stop a hostile producer that bypasses the framework.** The payload limit is enforced when the
  framework serializes a message; a raw producer can still send a large message to your broker.
  Configure the broker's own message size limit.
- **encrypt anything by default.** Body encryption is opt-in, and transport encryption (TLS) is set by
  your connection strings and broker configuration.
- **authorize HTTP requests.** Authentication and authorization are your application's, configured in
  ASP.NET Core; the framework applies the scope it is given.
- **protect secrets in your configuration.** Keep connection strings and keys in a secret store.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).
