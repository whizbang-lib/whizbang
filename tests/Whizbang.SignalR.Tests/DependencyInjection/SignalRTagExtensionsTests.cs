using Microsoft.AspNetCore.SignalR;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;
using Whizbang.SignalR.DependencyInjection;

namespace Whizbang.SignalR.Tests.DependencyInjection;

/// <summary>
/// Covers SignalRTagExtensions.UseSignalR{THub} overloads. The whole class
/// sat at 0% in coverage — its only callers are application Program.cs
/// files that the test suite doesn't exercise.
/// </summary>
public class SignalRTagExtensionsTests {

  /// <summary>A stand-in SignalR hub for type parameter binding.</summary>
  public class TestHub : Hub { }

  [Test]
  public async Task UseSignalR_DefaultPriority_ReturnsSameOptionsAsync() {
    var options = new TagOptions();
    var returned = options.UseSignalR<TestHub>();
    await Assert.That(returned).IsSameReferenceAs(options);
  }

  [Test]
  public async Task UseSignalR_WithPriority_ReturnsSameOptionsAsync() {
    var options = new TagOptions();
    var returned = options.UseSignalR<TestHub>(priority: 50);
    await Assert.That(returned).IsSameReferenceAs(options);
  }
}
