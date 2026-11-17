// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using OpenTelemetry.Trace;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Observability.DependencyInjection;

namespace Whizbang.Observability.Tests.DependencyInjection;

/// <summary>
/// Locks the turnkey tracing subscription to <see cref="WhizbangActivitySource.All"/> (#2365), as the meter one is locked
/// to <see cref="WhizbangMeters.All"/>: a source added later, such as <c>Whizbang.Integrity</c>, is subscribed with no
/// consumer edit.
/// </summary>
/// <code-under-test>src/Whizbang.Observability/DependencyInjection/WhizbangTracingRegistration.cs</code-under-test>
public class WhizbangTracingRegistrationTests {
  private sealed class RecordingTracerProviderBuilder : TracerProviderBuilder {
    public List<string> Names { get; } = [];

    public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory) => this;

    public override TracerProviderBuilder AddSource(params string[] names) {
      Names.AddRange(names);
      return this;
    }

    public override TracerProviderBuilder AddLegacySource(string operationName) => this;
  }

  [Test]
  public async Task AddWhizbangInstrumentation_SubscribesEverySource_IntegrityIncludedAsync() {
    var builder = new RecordingTracerProviderBuilder();

    var returned = builder.AddWhizbangInstrumentation();

    await Assert.That(returned).IsSameReferenceAs(builder);
    await Assert.That(builder.Names).IsEquivalentTo(WhizbangActivitySource.All);
    await Assert.That(builder.Names).Contains(IntegrityTracing.SOURCE_NAME);
  }

  [Test]
  public async Task AddWhizbangInstrumentation_NullBuilder_ThrowsAsync() {
    TracerProviderBuilder? builder = null;

    await Assert.That(() => builder!.AddWhizbangInstrumentation()).Throws<ArgumentNullException>();
  }
}
