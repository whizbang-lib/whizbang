// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests;

/// <summary>
/// Every method that parses JSON into a <see cref="JsonElement"/> it hands back keeps that element
/// readable after the call returns and after a full garbage collection, with exactly the content it
/// was built from (#1679). The methods once took the element from a <see cref="JsonDocument"/> they
/// never disposed; they now parse with <see cref="JsonElement.Parse(string, JsonDocumentOptions)"/>,
/// whose element owns its memory, and these tests hold the caller-visible contract across that change.
/// </summary>
/// <remarks>
/// Each element is produced inside a method that is never inlined, so nothing the method allocated is
/// reachable from the test when the collection runs; only the returned element survives.
/// </remarks>
[Category("Serialization")]
public class ParsedJsonElementLifetimeTests {
  [Test]
  public async Task JsonElementHelper_FromString_ElementOutlivesTheCallAsync() {
    var element = _afterCollection(() => JsonElementHelper.FromString("a\"b\\c\n"));

    await Assert.That(element.GetRawText()).IsEqualTo("\"a\\\"b\\\\c\\n\"");
    await Assert.That(element.GetString()).IsEqualTo("a\"b\\c\n");
  }

  [Test]
  public async Task JsonElementHelper_FromNullString_ElementOutlivesTheCallAsync() {
    var element = _afterCollection(() => JsonElementHelper.FromString(null));

    await Assert.That(element.ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(element.GetRawText()).IsEqualTo("null");
  }

  [Test]
  public async Task JsonElementHelper_FromInt32_ElementOutlivesTheCallAsync() {
    var element = _afterCollection(() => JsonElementHelper.FromInt32(-42));

    await Assert.That(element.GetRawText()).IsEqualTo("-42");
    await Assert.That(element.GetInt32()).IsEqualTo(-42);
  }

  [Test]
  public async Task JsonElementHelper_FromBoolean_ElementOutlivesTheCallAsync() {
    var element = _afterCollection(() => JsonElementHelper.FromBoolean(true));

    await Assert.That(element.GetRawText()).IsEqualTo("true");
  }

  [Test]
  public async Task JsonElementHelper_FromStringArray_ElementOutlivesTheCallAsync() {
    var element = _afterCollection(() => JsonElementHelper.FromStringArray(["x", "y\"z"]));

    await Assert.That(element.GetRawText()).IsEqualTo("[\"x\",\"y\\\"z\"]");
    await Assert.That(element.GetArrayLength()).IsEqualTo(2);
  }

  [Test]
  public async Task AuditJsonSerializer_CompileTimeType_ElementOutlivesTheCallAsync() {
    var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    var element = _afterCollection(() => AuditJsonSerializer.SerializeToJsonElement(new LifetimePayload("kept"), options));

    await Assert.That(element.GetRawText()).IsEqualTo("{\"Detail\":\"kept\"}");
  }

  [Test]
  public async Task AuditJsonSerializer_RuntimeTypeFallback_ElementOutlivesTheCallAsync() {
    var options = new JsonSerializerOptions { TypeInfoResolver = new RuntimeOnlyResolver() };
    ILifetimePayload value = new LifetimePayload("runtime");

    var element = _afterCollection(() => AuditJsonSerializer.SerializeToJsonElement(value, options));

    await Assert.That(element.GetRawText()).IsEqualTo("{\"Detail\":\"runtime\"}");
  }

  [Test]
  public async Task AuditJsonSerializer_UnregisteredType_EmptyObjectOutlivesTheCallAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var element = _afterCollection(() => AuditJsonSerializer.SerializeToJsonElement(new LifetimePayload("lost"), options));

    await Assert.That(element.GetRawText()).IsEqualTo("{}");
  }

  [Test]
  public async Task ControlMessageTtl_Stamp_MetadataElementOutlivesTheCallAsync() {
    var destination = new TransportDestination("control", "route");

    var stamped = _afterCollection(() => ControlMessageTtl.Stamp(destination, TimeSpan.FromSeconds(1.5)));

    await Assert.That(stamped.Metadata![ControlMessageTtl.MetadataKey].GetRawText()).IsEqualTo("1.5");
  }

  [Test]
  public async Task ControlPlaneDestination_WithSession_StreamIdElementOutlivesTheCallAsync() {
    var streamId = Guid.Parse("0190f7a2-0000-7000-8000-000000000001");
    var destination = new TransportDestination("control", "route");

    var withSession = _afterCollection(() => ControlPlaneDestination.WithSession(destination, streamId));

    await Assert.That(withSession.Metadata!["StreamId"].GetRawText())
      .IsEqualTo("\"0190f7a2-0000-7000-8000-000000000001\"");
  }

  /// <summary>
  /// Runs <paramref name="produce"/> in a frame of its own, then collects twice (the second pass
  /// reclaims anything a finalizer released), and returns what it produced.
  /// </summary>
  private static T _afterCollection<T>(Func<T> produce) {
    var result = _produce(produce);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    return result;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static T _produce<T>(Func<T> produce) => produce();

  private interface ILifetimePayload;

  private sealed record LifetimePayload(string Detail) : ILifetimePayload;

  /// <summary>Refuses the interface so the compile-time lookup fails and the runtime type is used.</summary>
  private sealed class RuntimeOnlyResolver : IJsonTypeInfoResolver {
    private readonly DefaultJsonTypeInfoResolver _inner = new();
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(ILifetimePayload) ? null : _inner.GetTypeInfo(type, options);
  }
}
