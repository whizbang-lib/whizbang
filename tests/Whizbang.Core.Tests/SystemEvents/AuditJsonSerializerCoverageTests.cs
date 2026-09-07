using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Coverage-round-23 targets for <see cref="AuditJsonSerializer"/>: the null-value short-circuit, and
/// the runtime-type fallback when the compile-time type isn't registered but the value's ACTUAL type
/// is. A wrong answer on either path either throws past a caller that legitimately has no value to
/// serialize (crashing the audit write instead of degrading it), or drops a serializable audit payload
/// to an empty <c>{}</c> object purely because it was passed through an <c>object</c>-typed call site —
/// a silent compliance gap the caller never asked for.
/// </summary>
public class AuditJsonSerializerCoverageTests {
  [Test]
  public async Task SerializeToJsonElement_NullValue_ReturnsDefaultJsonElementWithoutThrowingAsync() {
    string? nullValue = null;

    var result = AuditJsonSerializer.SerializeToJsonElement(nullValue, JsonContextRegistry.CreateCombinedOptions());

    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Undefined)
      .Because("a null audit source (e.g. an optional field on the event being audited) must short-circuit "
        + "to an empty/default element instead of a NullReferenceException taking down the audit write");
  }

  // The compile-time type must genuinely fail to resolve while the runtime type resolves. An
  // `object`-typed local does NOT do that: GetTypeInfo(typeof(object)) succeeds, so the value
  // serializes in the FIRST block and the fallback never runs -- a test asserting only the output
  // value passes without exercising the path it names. A private local type is unregistered in
  // every source-gen context, and its own runtime type is equally unregistered, so both blocks
  // fail and the method lands on the empty-object compliance gap instead.
  //
  // What this path actually protects: an audit call site holding a payload through an unregistered
  // static type, whose concrete value IS serializable. Losing the fallback turns that into a
  // silent `{}` -- the audit row is written, looks valid, and carries none of the evidence.
  [Test]
  public async Task SerializeToJsonElement_UnregisteredCompileTimeType_StillSerializesViaTheRuntimeTypeAsync() {
    var options = new JsonSerializerOptions { TypeInfoResolver = new _runtimeOnlyResolver() };
    IAuditPayload value = new _auditPayload("evidence");

    var result = AuditJsonSerializer.SerializeToJsonElement(value, options);

    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Object)
      .Because("the runtime-type fallback must actually serialize, not degrade to the empty-object "
             + "compliance gap, when the concrete type is perfectly serializable");
    await Assert.That(result.GetProperty("Detail").GetString()).IsEqualTo("evidence")
      .Because("the fallback must serialize the ACTUAL value; an empty object here is an audit row "
             + "that looks written and carries no evidence");
  }

  private interface IAuditPayload;

  private sealed record _auditPayload(string Detail) : IAuditPayload;

  /// <summary>
  /// Resolves the concrete payload but refuses its interface, which is the shape that forces the
  /// compile-time lookup to fail and the runtime-type lookup to succeed.
  /// </summary>
  private sealed class _runtimeOnlyResolver : IJsonTypeInfoResolver {
    private readonly DefaultJsonTypeInfoResolver _inner = new();
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(IAuditPayload) ? null : _inner.GetTypeInfo(type, options);
  }
}
