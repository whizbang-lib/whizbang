// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.AutoPopulate;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Branch coverage for <see cref="AutoPopulateRegistry.FindRegistrationsByTypeName"/>: a
/// registration whose message type has no CLR full name (an unbound generic parameter registered
/// by mistake) must never match a lookup, and must not stop the walk from finding the
/// legitimately named registrations beside it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/AutoPopulate/AutoPopulateRegistry.cs</code-under-test>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateRegistryBranchCoverageTests {

  /// <summary>Never constructed; only its unbound generic parameter's <see cref="Type"/> (whose
  /// <c>FullName</c> is null) is registered, standing in for a malformed registration.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S2326:Unused type parameters should be removed", Justification = "An open generic type is the shape under test; the parameter carries no data by design.")]
  private sealed class NamelessHolder<T>;

  private sealed record NamedProbeMessage(Guid Id);

  private sealed class TwoRegistrationRegistry(params AutoPopulateRegistration[] registrations) : IAutoPopulateRegistry {
    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) =>
      registrations.Where(r => r.MessageType == messageType);
    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() => registrations;
  }

  [Test]
  public async Task FindRegistrationsByTypeName_TypeWithoutFullName_NeverMatchesAndDoesNotHideSiblingsAsync() {
    var namelessType = typeof(NamelessHolder<>).GetGenericArguments()[0];
    var nameless = new AutoPopulateRegistration {
      MessageType = namelessType,
      PropertyName = "NamelessStamp",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp
    };
    var named = new AutoPopulateRegistration {
      MessageType = typeof(NamedProbeMessage),
      PropertyName = "NamedStamp",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp
    };
    // The nameless registration comes first so the walk has to get past it to reach the sibling.
    AutoPopulateRegistry.Register(new TwoRegistrationRegistry(nameless, named), priority: 9221);

    var byParameterName = AutoPopulateRegistry.FindRegistrationsByTypeName(namelessType.Name).ToList();
    var bySiblingName = AutoPopulateRegistry.FindRegistrationsByTypeName(typeof(NamedProbeMessage).FullName!).ToList();

    await Assert.That(byParameterName.Contains(nameless)).IsFalse()
      .Because("a type with no full name has no name to match; treating its short name as one would let "
        + "a malformed registration answer lookups meant for a real type");
    await Assert.That(bySiblingName.Contains(named)).IsTrue()
      .Because("the nameless registration is skipped, not a reason to abandon the rest of the registry");
    await Assert.That(bySiblingName.Contains(nameless)).IsFalse();
  }
}
