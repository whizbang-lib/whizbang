// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Configuration;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The <see cref="GuidOrderingValidator"/> registration <c>AddWhizbang</c> makes (#2528): it reads the configured
/// options, logger and metrics when the container has them, and falls back to the default options (severity Error) and
/// no logging when it does not.
/// </summary>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingValidatorRegistrationTests {
  [Test]
  public async Task Registration_ContainerWithNoOptionsOrLogger_BuildsAValidatorWithTheDefaultSeverityAsync() {
    var factory = _validatorFactory();
    await using var bare = new ServiceCollection().BuildServiceProvider();

    var validator = (GuidOrderingValidator)factory(bare);

    await Assert.That(() => validator.ValidateMessageId(TrackedGuid.NewRandom(), "Order", "Test"))
      .ThrowsExactly<GuidOrderingException>()
      .Because("with no options registered the default WhizbangOptions apply, and their severity is Error");
  }

  [Test]
  public async Task Registration_ContainerWithOptionsAndLogger_BuildsAValidatorWithTheConfiguredSeverityAsync() {
    var factory = _validatorFactory();
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<WhizbangOptions>(o => o.GuidOrderingViolationSeverity = GuidOrderingSeverity.Warning);
    await using var provider = services.BuildServiceProvider();

    var validator = (GuidOrderingValidator)factory(provider);

    await Assert.That(() => validator.ValidateMessageId(TrackedGuid.NewRandom(), "Order", "Test"))
      .ThrowsNothing()
      .Because("the configured Warning severity is read from the registered options, so a non-v7 id is logged, not refused");
  }

  private static Func<IServiceProvider, object> _validatorFactory() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    _ = services.AddWhizbang();
    var descriptor = services.Single(d => d.ServiceType == typeof(GuidOrderingValidator));
    return descriptor.ImplementationFactory!;
  }
}
