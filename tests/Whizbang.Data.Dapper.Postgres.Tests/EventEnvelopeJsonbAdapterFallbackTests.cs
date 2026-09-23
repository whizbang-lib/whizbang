using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// What <see cref="EventEnvelopeJsonbAdapter"/> does when a row does not look the way the current
/// writer would have written it. Every event ever stored is read back through this class, so its
/// tolerance for older and odder rows is the compatibility guarantee of the store itself — and the
/// existing suite only exercises rows this same version just wrote.
/// </summary>
/// <tests>EventEnvelopeJsonbAdapter</tests>
public class EventEnvelopeJsonbAdapterFallbackTests {

  private static EventEnvelopeJsonbAdapter _createAdapter() =>
    new(JsonOptionsHelper.CreateOptions());

  private static MessageEnvelope<TestEvent> _envelope(string? tenantId = null, string? userId = null) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = Guid.NewGuid(), Payload = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1
          },
          Topic = "test-topic",
          Timestamp = DateTimeOffset.UtcNow,
          // Null when no ids are given, so a test can make the scope COLUMN the only source of
          // scope. The hop round-trips its own scope through metadata, so a hop that carries one
          // would satisfy GetCurrentScope() regardless of what the column parser did.
          Scope = tenantId is null && userId is null
            ? null
            : ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenantId, UserId = userId })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  /// <summary>Rewrites a real persistence model's scope column, leaving data and metadata intact.</summary>
  private static JsonbPersistenceModel _withScope(JsonbPersistenceModel model, string? scopeJson) =>
    new() { DataJson = model.DataJson, MetadataJson = model.MetadataJson, ScopeJson = scopeJson };

  [Test]
  public async Task NonGenericFromJsonb_RefusesAndNamesTheGenericOverloadToUseAsync() {
    // This overload exists to satisfy the interface and can never work: reconstructing an envelope
    // needs the concrete message type at compile time, which is the whole basis of the store's AOT
    // support. Throwing is correct -- but the message has to say what to call instead, because the
    // caller reaching this has a JsonbPersistenceModel in hand and no obvious next move.
    var adapter = _createAdapter();
    var jsonb = adapter.ToJsonb(_envelope());

    await Assert.That(() => adapter.FromJsonb(jsonb))
      .Throws<NotSupportedException>()
      .WithMessageContaining("FromJsonb<TMessage>")
      .Because("a refusal that does not name the working alternative sends the caller to the source to find it");
  }

  [Test]
  public async Task MetadataWrittenWithoutAHopsKey_ReadsBackAsAnEnvelopeWithNoHopsAsync() {
    // Rows written before hops were persisted have no "hops" key at all -- not an empty array, no
    // key. Reading one must yield an envelope with no hops rather than throwing: these are real
    // rows in real event stores, and an event store that cannot read its own history is not one.
    var adapter = _createAdapter();
    var original = adapter.ToJsonb(_envelope());

    var metadata = JsonNode.Parse(original.MetadataJson)!.AsObject();
    metadata.Remove("hops");
    var legacyRow = new JsonbPersistenceModel {
      DataJson = original.DataJson,
      MetadataJson = metadata.ToJsonString(),
      ScopeJson = null
    };

    var restored = adapter.FromJsonb<TestEvent>(legacyRow);

    await Assert.That(restored.Hops).IsEmpty()
      .Because("a row that never stored hops has none to restore, and inventing one would fabricate provenance");
    await Assert.That(restored.MessageId.Value).IsNotEqualTo(Guid.Empty)
      .Because("the rest of the envelope must still come back -- a missing hops key is not a corrupt row");
  }

  [Test]
  public async Task ScopeJsonWithAWrongTypedField_FallsBackInsteadOfFailingTheReadAsync() {
    // A scope column whose shape does not match PerspectiveScope -- here a numeric tenant where a
    // string belongs -- must not fail the read. The event itself is intact; only the scope is
    // unreadable. Failing here would make one malformed column poison every replay of that event,
    // and the retry would never succeed because the row never changes.
    var adapter = _createAdapter();
    var row = _withScope(adapter.ToJsonb(_envelope()), """{"t":123}""");

    var restored = adapter.FromJsonb<TestEvent>(row);

    await Assert.That(restored.GetCurrentScope()).IsNull()
      .Because("an unreadable scope yields no scope; guessing one would attach the wrong tenant to a replayed event");
  }

  [Test]
  public async Task ScopeJsonThatIsLiteralNull_LeavesTheEnvelopeUnscopedAsync() {
    // "null" is a legal JSON document and a plausible value for a nullable column serialized by an
    // older writer. Both parsers must treat it as "no scope" rather than dereferencing the null
    // they get back.
    var adapter = _createAdapter();
    var row = _withScope(adapter.ToJsonb(_envelope()), "null");

    var restored = adapter.FromJsonb<TestEvent>(row);

    await Assert.That(restored.GetCurrentScope()).IsNull()
      .Because("a literal null scope is an absent scope, not a parse failure");
  }

  /// <summary>
  /// A resolver that answers every type except <see cref="PerspectiveScope"/> — the shape of a host
  /// whose JSON context predates the short-key scope format, or was assembled without it.
  /// </summary>
  private sealed class ResolverWithoutPerspectiveScope(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(PerspectiveScope) ? null : inner.GetTypeInfo(type, options);
  }

  [Test]
  public async Task ScopeColumnReadByAContextWithoutPerspectiveScope_StillParsesTheLegacyFormatAsync() {
    // The short-key parser is written as a TRY: when it cannot read the column, the caller falls
    // through to the legacy snake_case parser. Asking the options for PerspectiveScope's metadata
    // outright defeated that -- an unregistered type throws rather than answering "no" -- so a host
    // whose context lacks PerspectiveScope failed the whole read of a legacy row it could otherwise
    // have understood perfectly. Every event in such a store replays through this path.
    var baseOptions = JsonOptionsHelper.CreateOptions();
    var withoutScopeType = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = new ResolverWithoutPerspectiveScope(baseOptions.TypeInfoResolver!)
    };
    var row = _withScope(
      _createAdapter().ToJsonb(_envelope()),
      """{"tenant_id":"legacy-tenant","user_id":"legacy-user"}""");

    var restored = new EventEnvelopeJsonbAdapter(withoutScopeType).FromJsonb<TestEvent>(row);

    var scope = restored.GetCurrentScope();
    await Assert.That(scope).IsNotNull()
      .Because("the legacy parser can read this column, so a missing short-key contract must not lose the scope");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("legacy-tenant");
    await Assert.That(scope.Scope.UserId).IsEqualTo("legacy-user");
  }
}
