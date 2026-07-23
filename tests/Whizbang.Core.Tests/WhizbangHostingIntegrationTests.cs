using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Covers the turnkey auto-encompass: <see cref="ServiceCollectionExtensions.AddWhizbang"/> invokes the
/// <see cref="ServiceRegistrationCallbacks.HostingIntegration"/> callback (which the ASP.NET assembly's
/// module initializer sets to <c>AddWhizbangAspNet</c>) by default, and skips it when the consumer opts
/// out via <c>AutoRegisterAspNetHosting = false</c>. NotInParallel — it manipulates the shared static.
/// </summary>
[NotInParallel]
public class WhizbangHostingIntegrationTests {

  [Test]
  public async Task AddWhizbang_InvokesHostingIntegration_ByDefault_SkipsWhenOptedOutAsync() {
    ServiceRegistrationCallbacks.Reset();
    try {
      var invokedByDefault = 0;
      ServiceRegistrationCallbacks.HostingIntegration = _ => invokedByDefault++;
      new ServiceCollection().AddWhizbang();
      await Assert.That(invokedByDefault).IsEqualTo(1);

      var invokedWhenOptedOut = 0;
      ServiceRegistrationCallbacks.HostingIntegration = _ => invokedWhenOptedOut++;
      new ServiceCollection().AddWhizbang(o => o.AutoRegisterAspNetHosting = false);
      await Assert.That(invokedWhenOptedOut).IsEqualTo(0);
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }
}
