// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// The partition assignment source a host with no storage driver gets (#1358): it never has an assignment, so a claim
/// ranks the live instances itself, exactly as a claim with no source registered always has. Every Postgres driver
/// registers the elected assigner's cache over it (#2559).
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
[Category("MultiStream")]
public class NullPartitionAssignmentSourceTests {
  [Test]
  public async Task ForClaimAsync_NeverHasAnAssignmentAsync() {
    var source = NullPartitionAssignmentSource.Instance;

    await Assert.That(await source.ForClaimAsync(Guid.CreateVersion7(), CancellationToken.None)).IsNull();
    await Assert.That(source.Current).IsNull();
  }

  [Test]
  public async Task MarkStaleAndSubscribing_ChangeNothingAsync() {
    var source = NullPartitionAssignmentSource.Instance;
    var raised = new List<PartitionAssignment?>();
    void Handler(PartitionAssignment? assignment) => raised.Add(assignment);

    source.OnAssignmentChanged += Handler;
    source.MarkStale();
    source.OnAssignmentChanged -= Handler;

    await Assert.That(raised).IsEmpty().Because("there is no assignment to change");
    await Assert.That(source).IsAssignableTo<INullDefault>();
  }

  [Test]
  public async Task TheCoreDefaults_RegisterIt_AsTheNullDefaultADriverDisplacesAsync() {
    // #2559: the core owns the placeholder, as it owns every storage-backed seam's; no driver registers a null source.
    var services = new ServiceCollection();

    services.TryAddWhizbangDefaults();

    var registered = services.Single(d => d.ServiceType == typeof(IPartitionAssignmentSource));
    await Assert.That(registered.ImplementationInstance).IsSameReferenceAs(NullPartitionAssignmentSource.Instance);
  }
}
