# Plan: unify RabbitMQ + ASB receive-decision flow

## Status: PROPOSED

## Why

Both transports already resolve envelope types through the same registry
(`JsonContextRegistry.GetTypeInfoByName` via `BodyClaimWireHelper`), but their *receive
decision* flow has diverged:

- **ASB** (`AsbReceiveDecisionMaker`): claim-detect → `GetTypeInfoByName` → **`ITypeBinder.Bind`
  fallback** on miss → **raw-receptor fallback** → else `AckAndDrop(reason)`. Returns a
  structured `AsbReceiveDecision`.
- **RabbitMQ** (`RabbitMQTransport._deserializeMessage` / `_deserializeMessageFromBody`):
  claim-detect → `ResolveDeserializeTypeInfo` → on miss just logs + returns null. **No
  typeBinder fallback, no raw-receptor path.**

Same broker message can therefore be handled (ASB) or silently dropped (RabbitMQ). The
resolution logic is also copy-pasted with subtle differences — a correctness + DRY hazard.

## Design — shared, transport-agnostic decision maker in Core

Extract the decision logic into a Core component both transports call:

```csharp
// Whizbang.Core (transport-agnostic)
public enum ReceiveAction { Deserialized, RawReceptor, AckAndDrop }

public readonly record struct MessageReceiveDecision(
    ReceiveAction Action,
    IMessageEnvelope? Envelope,       // when Deserialized
    string? EnvelopeTypeName,
    string? Reason);                  // when AckAndDrop / RawReceptor

public interface IMessageReceiveResolver {
  MessageReceiveDecision Resolve(
      string envelopeTypeName,
      string? isClaimHeaderValue,
      string bodyJson,
      JsonSerializerOptions jsonOptions,
      ITypeBinder? typeBinder,
      IRawReceptorRegistry? rawReceptorRegistry);
}
```

- The resolver encapsulates the full ASB flow (claim → registry → typeBinder → raw-receptor →
  ack/drop), which is the superset. Both transports delegate to it.
- Each transport keeps ONLY its wire-specific concerns: reading headers
  (`byte[]` over AMQP for RabbitMQ; `ApplicationProperties` for ASB) and mapping
  `MessageReceiveDecision` onto its ack/nack/dead-letter primitives.
- RabbitMQ thereby gains the typeBinder + raw-receptor fallbacks (the behavior change — gated
  + tested).

## Execution (TDD)

- [ ] Extract `IMessageReceiveResolver` + default impl in Core from `AsbReceiveDecisionMaker`;
      port ASB to delegate (behavior-preserving; ASB tests stay green).
- [ ] Port RabbitMQ `_deserializeMessage*` to delegate; add RED tests for the now-supported
      typeBinder + raw-receptor cases that previously dropped.
- [ ] Parity test: identical (envelopeType, claim, body) inputs yield the same
      `MessageReceiveDecision` on both transports' adapters.

## Risk

RabbitMQ receive semantics change (drop → bind/raw-handle on registry miss). Gate behind the
existing raw-receptor/type-binder registration so consumers without them see no behavior change.
