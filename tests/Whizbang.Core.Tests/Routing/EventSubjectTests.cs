// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Routing.ExcludedEventFixtures;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The subject an event is published under (#2175): one rendering shared by the publish path and the
/// excluded-event-types rule.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/EventSubject.cs</code-under-test>
public class EventSubjectTests {
  [Test]
  public async Task For_WireForm_IsTheLowercasedClrFormAsync() {
    await Assert.That(EventSubject.For("MyApp.Events.Outer+OrderPlaced, MyApp.Contracts"))
      .IsEqualTo("myapp.events.outer+orderplaced");
  }

  [Test]
  public async Task For_VersionedForm_DropsTheAssemblyDetailsAsync() {
    await Assert.That(EventSubject.For("MyApp.Events.OrderPlaced, MyApp, Version=1.0.0.0, Culture=neutral"))
      .IsEqualTo("myapp.events.orderplaced");
  }

  [Test]
  public async Task For_ClrForm_IsLowercasedAsync() {
    await Assert.That(EventSubject.For("MyApp.Events.OrderPlaced")).IsEqualTo("myapp.events.orderplaced");
  }

  [Test]
  public async Task For_Type_IsTheSubjectOfItsWireFormAsync() {
    var type = typeof(Container.NestedNoisyEvent);

    await Assert.That(EventSubject.For(type)).IsEqualTo(EventSubject.For(TypeNameFormatter.Format(type)));
    await Assert.That(EventSubject.For(type))
      .IsEqualTo("whizbang.core.tests.routing.excludedeventfixtures.container+nestednoisyevent");
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task For_BlankName_ThrowsAsync(string name) {
    await Assert.That(() => EventSubject.For(name)).Throws<ArgumentException>();
  }

  [Test]
  public async Task For_NullType_ThrowsAsync() {
    await Assert.That(() => EventSubject.For((Type)null!)).Throws<ArgumentNullException>();
  }
}
