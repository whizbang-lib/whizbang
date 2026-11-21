// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The names the perspective runner gives an event's type (#2469): the forms the generated runners read off the type
/// directly, kept unchanged when the runner logic moved into Whizbang.Core. A generic type parameter is the type with no
/// CLR full name, the case each form has its own fallback for.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerTypeNames.cs</code-under-test>
public class PerspectiveRunnerTypeNamesTests {
  private sealed record NamedEvent : IEvent;

  private static readonly Type _noFullName = typeof(List<>).GetGenericArguments()[0];

  [Test]
  public async Task Recorded_IsTheClrFullName_OrTheSimpleNameWithoutOneAsync() {
    await Assert.That(PerspectiveRunnerTypeNames.Recorded(typeof(NamedEvent)))
      .IsEqualTo("Whizbang.Core.Tests.Perspectives.PerspectiveRunnerTypeNamesTests+NamedEvent");
    await Assert.That(PerspectiveRunnerTypeNames.Recorded(_noFullName)).IsEqualTo("T");
  }

  [Test]
  public async Task ReceptorKey_IsTheClrFullName_OrEmptyWithoutOneAsync() {
    await Assert.That(PerspectiveRunnerTypeNames.ReceptorKey(typeof(NamedEvent)))
      .IsEqualTo("Whizbang.Core.Tests.Perspectives.PerspectiveRunnerTypeNamesTests+NamedEvent");
    await Assert.That(PerspectiveRunnerTypeNames.ReceptorKey(_noFullName)).IsEmpty();
  }

  [Test]
  public async Task SpanTag_IsTheClrFullName_OrNullWithoutOneAsync() {
    await Assert.That(PerspectiveRunnerTypeNames.SpanTag(typeof(NamedEvent)))
      .IsEqualTo("Whizbang.Core.Tests.Perspectives.PerspectiveRunnerTypeNamesTests+NamedEvent");
    await Assert.That(PerspectiveRunnerTypeNames.SpanTag(_noFullName)).IsNull();
  }
}
