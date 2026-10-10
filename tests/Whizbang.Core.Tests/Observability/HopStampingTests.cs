// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Each value a hop or stored envelope is stamped with, and the fallback each takes when its source
/// has nothing to give: no ambient activity, no instance identity, no hop list.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/HopStamping.cs</code-under-test>
[Category("Observability")]
public class HopStampingTests {
  [Test]
  public async Task AmbientTraceParent_NoActivity_IsNullAsync() {
    using var _ = new ActivityScope(null);

    await Assert.That(HopStamping.AmbientTraceParent).IsNull();
  }

  [Test]
  public async Task AmbientTraceParent_RunningActivity_IsItsIdAsync() {
    using var _ = new ActivityScope(null);
    using var activity = new Activity("hop").SetIdFormat(ActivityIdFormat.W3C).Start();

    await Assert.That(HopStamping.AmbientTraceParent).IsEqualTo(activity.Id);
    await Assert.That(activity.Id).StartsWith("00-")
      .Because("a hop carries the W3C traceparent, so downstream spans join the same trace");
  }

  [Test]
  public async Task ToInfoOrUnknown_NoProvider_IsUnknownAsync() {
    IServiceInstanceProvider? provider = null;

    await Assert.That(provider.ToInfoOrUnknown()).IsSameReferenceAs(ServiceInstanceInfo.Unknown);
  }

  [Test]
  public async Task ToInfoOrUnknown_ProviderWithNoInfo_IsUnknownAsync() {
    var provider = new InfoProvider(null);

    await Assert.That(provider.ToInfoOrUnknown()).IsSameReferenceAs(ServiceInstanceInfo.Unknown);
  }

  [Test]
  public async Task ToInfoOrUnknown_Provider_IsItsInfoAsync() {
    var info = new ServiceInstanceInfo { ServiceName = "S", InstanceId = Guid.NewGuid(), HostName = "h", ProcessId = 1 };

    await Assert.That(new InfoProvider(info).ToInfoOrUnknown()).IsSameReferenceAs(info);
  }

  [Test]
  public async Task CopyHops_Hops_IsADetachedCopyAsync() {
    var hop = new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown };
    var envelope = _envelope([hop]);

    var copy = envelope.CopyHops();
    envelope.Hops.Add(new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown });

    await Assert.That(copy).IsNotSameReferenceAs(envelope.Hops);
    await Assert.That(copy.Count).IsEqualTo(1);
    await Assert.That(copy[0]).IsSameReferenceAs(hop);
  }

  [Test]
  public async Task CopyHops_NoHopList_IsEmptyAsync() {
    var copy = _envelope(null!).CopyHops();

    await Assert.That(copy).IsEmpty();
  }

  [Test]
  public async Task CurrentScope_Scoped_IsItAsync() {
    var envelope = _envelope([new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "t-1" })
    }]);

    await Assert.That(envelope.GetCurrentPerspectiveScope()!.TenantId).IsEqualTo("t-1");
  }

  [Test]
  public async Task CurrentScope_Unscoped_NullAsync() {
    var envelope = _envelope([new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]);

    await Assert.That(envelope.GetCurrentPerspectiveScope()).IsNull();
  }

  private static MessageEnvelope<JsonElement> _envelope(List<MessageHop> hops) => new() {
    MessageId = MessageId.New(),
    Payload = default,
    Hops = hops,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed class InfoProvider(ServiceInstanceInfo? info) : IServiceInstanceProvider {
    public Guid InstanceId => Guid.Empty;
    public string ServiceName => "S";
    public string HostName => "h";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => info!;
  }

  /// <summary>Sets <see cref="Activity.Current"/> for the test and restores it after.</summary>
  private sealed class ActivityScope : IDisposable {
    private readonly Activity? _previous = Activity.Current;

    public ActivityScope(Activity? current) => Activity.Current = current;

    public void Dispose() => Activity.Current = _previous;
  }
}
