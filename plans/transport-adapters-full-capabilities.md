# Transport Adapters with Full Capabilities - Implementation Plan

**Status**: 🟡 In Progress
**Started**: 2025-11-08
**Target Version**: v0.3.0

---

## Overview

Implement 4 production-ready transport adapters with **ALL 6 capabilities** from `TransportCapabilities` enum, leveraging Whizbang's existing 18 interfaces and adding minimal new abstractions.

## Goals

- ✅ **Reuse all 18 existing interfaces** (ITransport, IMessageEnvelope, ISequenceProvider, etc.)
- ✅ **Add only 4 new messaging interfaces** (IInbox, IOutbox, IRequestResponseStore, IEventStore)
- ✅ **ORM-agnostic database layer** (swap EF Core, Dapper, NHibernate)
- ✅ **In-memory implementations** for all interfaces (fast testing, local dev)
- ✅ **Contract tests** following established pattern
- ✅ **TDD methodology** (RED → GREEN → REFACTOR)
- ✅ **100% test coverage**
- ✅ **No breaking changes** to existing tests

---

## Target Capability Matrix

| Transport | RequestResponse | PublishSubscribe | Streaming | Reliable | Ordered | ExactlyOnce |
|-----------|----------------|------------------|-----------|----------|---------|-------------|
| **Kafka** | ✅* | ✅ | ✅ | ✅ | ✅ | ✅ |
| **RabbitMQ** | ✅ | ✅ | ✅* | ✅ | ✅ | ✅* |
| **Service Bus** | ✅ | ✅ | ✅* | ✅ | ✅ | ✅ |
| **Event Hubs** | ✅* | ✅ | ✅ | ✅ | ✅ | ✅* |

*Requires supplementary infrastructure (IInbox/IOutbox/IRequestResponseStore/IEventStore)

---

## Existing Infrastructure (Reuse)

### 18 Existing Interfaces ✅

**Domain/Application (2)**
- `ICommand` - Marker for command messages
- `IEvent` - Marker for event messages

**Messaging/Dispatch (4)**
- `IDispatcher` - Routes messages to handlers
- `IReceptor<TMessage, TResponse>` - Stateless message handlers
- `IReceptor<TMessage>` - Zero-allocation handlers
- `IPerspectiveOf<TEvent>` - Event projections/read models

**Context & Delivery (2)**
- `IMessageContext` - Message metadata (MessageId, CorrelationId, CausationId)
- `IDeliveryReceipt` - Delivery tracking

**Observability (2)**
- `IMessageEnvelope` - Message wrapper with hops/metadata
- `ITraceStore` - Message trace storage (for debugging, NOT event sourcing)

**Pipeline (1)**
- `IPipelineBehavior<TRequest, TResponse>` - Cross-cutting concerns

**Transport (4)**
- `ITransport` - Core transport abstraction
- `ISubscription` - Subscription control
- `IMessageSerializer` - Envelope serialization
- `ITransportManager` - Multi-transport management

**Execution (1)**
- `IExecutionStrategy` - Serial/Parallel execution

**Policy (1)**
- `IPolicyEngine` - Policy matching

**Partitioning (1)**
- `IPartitionRouter` - Stream-to-partition routing

**Sequencing (1)**
- `ISequenceProvider` - Monotonic sequence generation

---

## New Infrastructure Needed

### 4 New Messaging Interfaces

1. **IInbox** - Deduplicates incoming messages (ExactlyOnce receiving)
2. **IOutbox** - Transactional outbox (ExactlyOnce sending)
3. **IRequestResponseStore** - Request/response correlation (RequestResponse for Kafka/Event Hubs)
4. **IEventStore** - Append-only event store (Streaming for RabbitMQ/Service Bus)

### 2 Database Abstraction Interfaces

1. **IDbConnectionFactory** - Database connection factory
2. **IDbExecutor** - Query/Execute abstraction (ORM-agnostic)

---

## Implementation Phases

### ✅ Phase 0: Core Messaging Infrastructure (TDD)
**Status**: ✅ Complete
**Started**: 2025-11-08
**Completed**: 2025-11-08

**Tasks:**
- [x] Create `IInbox` interface
- [x] Create `IOutbox` interface
- [x] Create `IRequestResponseStore` interface
- [x] Create `IEventStore` interface
- [x] Implement `InMemoryInbox`
- [x] Implement `InMemoryOutbox`
- [x] Implement `InMemoryRequestResponseStore`
- [x] Implement `InMemoryEventStore`
- [x] Write `InboxContractTests` (TDD RED)
- [x] Write `OutboxContractTests` (TDD RED)
- [x] Write `RequestResponseStoreContractTests` (TDD RED)
- [x] Write `EventStoreContractTests` (TDD RED)
- [x] Implement in-memory versions to pass tests (TDD GREEN)
- [x] Run `dotnet format` (TDD REFACTOR)
- [x] Verify all tests pass

**Deliverables:**
- 4 new interfaces in `src/Whizbang.Core/Messaging/`
- 4 in-memory implementations
- 4 contract test files
- All tests passing

**Test Count Target**: +40 tests
**Actual Test Count**: +36 tests (6 value object null tests removed - MessageId/CorrelationId are structs) (10 per interface)

---

### 🔲 Phase 1: Database Abstraction & Dapper (TDD)
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Create `IDbConnectionFactory` interface
- [ ] Create `IDbExecutor` interface
- [ ] Create `Whizbang.Data.Dapper` project
- [ ] Create SQL migration scripts (PostgreSQL/SQL Server/SQLite)
- [ ] Implement `DapperDbExecutor`
- [ ] Implement `DapperInbox` (passes InboxContractTests)
- [ ] Implement `DapperOutbox` (passes OutboxContractTests)
- [ ] Implement `DapperRequestResponseStore` (passes contract tests)
- [ ] Implement `DapperEventStore` (passes contract tests)
- [ ] Implement `DapperSequenceProvider` (passes SequenceProviderContractTests)
- [ ] Run `dotnet format`
- [ ] Verify all tests pass

**Deliverables:**
- 2 new interfaces in `src/Whizbang.Core/Data/`
- New project: `src/Whizbang.Data.Dapper/`
- SQL migration scripts
- 5 Dapper implementations
- Database integration tests

**Test Count Target**: +50 tests

---

### 🔲 Phase 2: Kafka Transport (TDD)
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Create `Whizbang.Transports.Kafka` project
- [ ] Write tests FIRST (TDD RED)
  - [ ] Basic Kafka transport tests
  - [ ] RequestResponse capability tests (uses IRequestResponseStore)
  - [ ] ExactlyOnce capability tests (uses IInbox)
- [ ] Implement `KafkaTransport` (ITransport) (TDD GREEN)
- [ ] Implement `KafkaRequestResponse` capability
- [ ] Implement `KafkaExactlyOnce` capability
- [ ] Add Aspire integration (Kafka emulator with KafkaUI)
- [ ] Run `dotnet format` (TDD REFACTOR)
- [ ] Verify all tests pass (in-memory + database)

**Deliverables:**
- New project: `src/Whizbang.Transports.Kafka/`
- Full ITransport implementation
- All 6 capabilities supported
- Aspire configuration

**Test Count Target**: +60 tests

---

### 🔲 Phase 3: RabbitMQ Transport (TDD)
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Create `Whizbang.Transports.RabbitMQ` project
- [ ] Write tests FIRST (TDD RED)
  - [ ] Basic RabbitMQ transport tests
  - [ ] Streaming capability tests (uses IEventStore + ISequenceProvider)
  - [ ] ExactlyOnce capability tests (uses IInbox + IOutbox)
- [ ] Implement `RabbitMQTransport` (ITransport) (TDD GREEN)
- [ ] Implement `RabbitMQStreaming` capability
- [ ] Implement `RabbitMQExactlyOnce` capability
- [ ] Add Aspire integration (RabbitMQ emulator with Management Plugin)
- [ ] Run `dotnet format` (TDD REFACTOR)
- [ ] Verify all tests pass (in-memory + database)

**Deliverables:**
- New project: `src/Whizbang.Transports.RabbitMQ/`
- Full ITransport implementation
- All 6 capabilities supported
- Aspire configuration

**Test Count Target**: +60 tests

---

### 🔲 Phase 4: Azure Service Bus Transport (TDD)
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Create `Whizbang.Transports.AzureServiceBus` project
- [ ] Write tests FIRST (TDD RED)
  - [ ] Basic Service Bus transport tests
  - [ ] Streaming capability tests (uses IEventStore + ISequenceProvider)
- [ ] Implement `AzureServiceBusTransport` (ITransport) (TDD GREEN)
- [ ] Implement `ServiceBusStreaming` capability
- [ ] Add Aspire integration (Service Bus emulator via RunAsEmulator())
- [ ] Run `dotnet format` (TDD REFACTOR)
- [ ] Verify all tests pass (in-memory + database)

**Deliverables:**
- New project: `src/Whizbang.Transports.AzureServiceBus/`
- Full ITransport implementation
- All 6 capabilities supported
- Aspire emulator configuration

**Test Count Target**: +50 tests

---

### 🔲 Phase 5: Azure Event Hub Transport (TDD)
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Create `Whizbang.Transports.AzureEventHub` project
- [ ] Write tests FIRST (TDD RED)
  - [ ] Basic Event Hub transport tests
  - [ ] RequestResponse capability tests (uses IRequestResponseStore)
  - [ ] ExactlyOnce capability tests (uses IInbox + IOutbox)
- [ ] Implement `AzureEventHubTransport` (ITransport) (TDD GREEN)
- [ ] Implement `EventHubRequestResponse` capability
- [ ] Implement `EventHubExactlyOnce` capability
- [ ] Add Aspire integration (Event Hub emulator)
- [ ] Run `dotnet format` (TDD REFACTOR)
- [ ] Verify all tests pass (in-memory + database)

**Deliverables:**
- New project: `src/Whizbang.Transports.AzureEventHub/`
- Full ITransport implementation
- All 6 capabilities supported
- Aspire emulator configuration

**Test Count Target**: +60 tests

---

### 🔲 Phase 6: Sample Integration & Documentation
**Status**: ⚪ Not Started

**Tasks:**
- [ ] Update `ECommerce.AppHost` with all emulators + PostgreSQL
- [ ] Update `ECommerce.ServiceDefaults` with transport configurations
- [ ] Create sample demonstrating all 6 capabilities on all 4 transports
- [ ] Document configuration patterns (emulator vs production)
- [ ] Document trade-offs and best practices
- [ ] Create README for local development setup
- [ ] Run full integration test suite
- [ ] Run `dotnet format`
- [ ] Verify all tests pass

**Deliverables:**
- Updated ECommerce sample
- Comprehensive documentation
- Working Aspire configuration
- Local dev setup guide

---

## Package Dependencies

### Added to Directory.Packages.props

**Database:**
- Npgsql 9.0.2
- Microsoft.Data.SqlClient 6.0.1
- Microsoft.Data.Sqlite 9.0.0
- Dapper 2.1.65

**Transport Clients:**
- Confluent.Kafka 2.12.0
- RabbitMQ.Client 7.2.0
- Azure.Messaging.ServiceBus 7.20.1
- Azure.Messaging.EventHubs 5.12.2
- Azure.Messaging.EventHubs.Processor 5.12.2

**Aspire (AppHost):**
- Aspire.Hosting.Kafka 9.5.2
- Aspire.Hosting.RabbitMQ 9.5.1
- Aspire.Hosting.Azure.ServiceBus 9.5.2
- Aspire.Hosting.Azure.EventHubs 9.5.2
- Aspire.Hosting.PostgreSQL 9.5.2

**Aspire (Client):**
- Aspire.Confluent.Kafka 9.5.2
- Aspire.RabbitMQ.Client 9.5.2
- Aspire.Azure.Messaging.ServiceBus 9.5.2
- Aspire.Azure.Messaging.EventHubs 9.5.2
- Aspire.Npgsql 9.5.2

---

## Test Count Tracking

| Phase | Target Tests | Actual Tests | Status |
|-------|-------------|--------------|--------|
| Phase 0 | +40 | +36 | ✅ Complete |
| Phase 1 | +50 | 0 | ⚪ Not Started |
| Phase 2 | +60 | 0 | ⚪ Not Started |
| Phase 3 | +60 | 0 | ⚪ Not Started |
| Phase 4 | +50 | 0 | ⚪ Not Started |
| Phase 5 | +60 | 0 | ⚪ Not Started |
| **Total** | **+320** | **+36** | - |
| **Current** | - | **694** | ✅ All Passing (692 passed, 2 skipped) |

---

## Success Criteria

- ✅ All 4 transports implement `ITransport` and support ALL 6 capabilities
- ✅ Reuse all 18 existing interfaces
- ✅ Add only 4 new messaging interfaces
- ✅ In-memory implementations for all new interfaces
- ✅ Dapper implementations for all new interfaces
- ✅ Contract tests for all new interfaces
- ✅ 100% test coverage for all new code
- ✅ All tests passing (658 existing + 320 new = 978 total)
- ✅ No existing tests broken or rewritten
- ✅ Aspire emulators configured for all transports
- ✅ Sample demonstrates all capabilities
- ✅ SQL migrations provided
- ✅ Code formatted with `dotnet format`

---

## Progress Log

### 2025-11-08
- ✅ Created implementation plan document
- ✅ Completed Phase 0: Core Messaging Infrastructure
  - Created 4 new interfaces: IInbox, IOutbox, IRequestResponseStore, IEventStore
  - Implemented 4 in-memory versions: InMemoryInbox, InMemoryOutbox, InMemoryRequestResponseStore, InMemoryEventStore
  - Created 4 contract test files with 36 tests total
  - All tests passing (TDD: RED → GREEN → REFACTOR)
  - Note: Removed 6 value object null validation tests (MessageId/CorrelationId are structs, cannot be null)

---

## Notes

- Following strict TDD: RED (write failing tests) → GREEN (implement) → REFACTOR (format)
- Using existing contract test pattern from ISequenceProvider and ITraceStore
- All in-memory implementations use ConcurrentDictionary for thread-safety
- Database implementations use Dapper (lightweight, fast, minimal dependencies)
- Aspire integration uses latest packages (v9.5.x)
