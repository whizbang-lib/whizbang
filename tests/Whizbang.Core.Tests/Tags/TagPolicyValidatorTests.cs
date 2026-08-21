using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="TagPolicyValidator"/> and its hosted-service host,
/// <see cref="TagPolicyStartupValidator"/>. Two invariants are enforced at host start:
/// the "sys-" tag prefix is reserved for framework tags (applications cannot mint new
/// sys-* tags), and a message type may match at most ONE coalesce binding (silent
/// first-match-wins is how policy drift hides).
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagPolicyValidatorTests {
  #region Reserved sys- prefix

  [Test]
  public async Task Validate_UserTagWithSysPrefix_ThrowsAsync() {
    // An application-declared tag minting a NEW sys-* tag must fail startup — the reserved
    // prefix is what keeps framework and application tag namespaces from ever colliding.
    var registrations = new[] { _registration(typeof(TestUserEvent), "sys-mine") };

    var ex = await Assert.That(() => TagPolicyValidator.Validate(registrations, _noBindings()))
      .Throws<TagPolicyConfigurationException>();

    await Assert.That(ex!.Message).Contains("sys-mine");
    await Assert.That(ex.Message).Contains(typeof(TestUserEvent).FullName!);
    await Assert.That(ex.Message).Contains("reserved");
  }

  [Test]
  public async Task Validate_FrameworkSysAuditOnFrameworkType_DoesNotThrowAsync() {
    // The framework's own sys-audit tag (carried by EventAudited from Whizbang.Core) is a
    // member of the SystemTags set and passes.
    var registrations = new[] { _registration(typeof(EventAudited), SystemTags.AUDIT) };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noBindings()))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_UserTagNamedAuditWithoutPrefix_DoesNotThrowAsync() {
    // "audit" (no prefix) is ordinary application vocabulary — only the "sys-" namespace
    // is reserved.
    var registrations = new[] { _registration(typeof(TestUserEvent), "audit") };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noBindings()))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_UserTypeCarryingTheFrameworkAuditTag_DoesNotThrowAsync() {
    // Applying an EXISTING framework tag to an application type is deliberate opt-in to that
    // framework policy (e.g. ride the audit coalesce cadence) — it mints nothing new, so it
    // is not a reservation violation.
    var registrations = new[] { _registration(typeof(TestUserEvent), SystemTags.AUDIT) };

    await Assert.That(() => TagPolicyValidator.Validate(registrations, _noBindings()))
      .ThrowsNothing();
  }

  #endregion

  #region Coalesce-binding ambiguity

  [Test]
  public async Task Validate_TypeMatchingTwoCoalesceBindings_ThrowsNamingTypeAndBothTagsAsync() {
    var registrations = new[] {
      _registration(typeof(TestUserEvent), "digest-a"),
      _registration(typeof(TestUserEvent), "digest-b")
    };
    var options = new TagOptions();
    options.Coalesce("digest-a", c => { });
    options.Coalesce("digest-b", c => { });

    var ex = await Assert.That(() => TagPolicyValidator.Validate(registrations, options.CoalesceBindings))
      .Throws<TagPolicyConfigurationException>();

    await Assert.That(ex!.Message).Contains(typeof(TestUserEvent).FullName!);
    await Assert.That(ex.Message).Contains("digest-a");
    await Assert.That(ex.Message).Contains("digest-b");
  }

  [Test]
  public async Task Validate_TypeMatchingOneCoalesceBinding_DoesNotThrowAsync() {
    var registrations = new[] {
      _registration(typeof(TestUserEvent), "digest-a"),
      _registration(typeof(TestUserEvent), "plain-tag")
    };
    var options = new TagOptions();
    options.Coalesce("digest-a", c => { });

    await Assert.That(() => TagPolicyValidator.Validate(registrations, options.CoalesceBindings))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_TwoTypesEachMatchingOneBinding_DoesNotThrowAsync() {
    // Two bindings in play is fine as long as no single TYPE matches both.
    var registrations = new[] {
      _registration(typeof(TestUserEvent), "digest-a"),
      _registration(typeof(TestOtherUserEvent), "digest-b")
    };
    var options = new TagOptions();
    options.Coalesce("digest-a", c => { });
    options.Coalesce("digest-b", c => { });

    await Assert.That(() => TagPolicyValidator.Validate(registrations, options.CoalesceBindings))
      .ThrowsNothing();
  }

  [Test]
  public async Task Validate_AmbiguityIgnoresDisabledBindingsAsync() {
    // SlideSeconds = 0 disables a group entirely (no stamp, no floor) — a disabled binding has
    // no operational effect, so it must not count toward ambiguity. This is also the operator's
    // escape hatch: disable one of two colliding bindings without touching message types.
    var registrations = new[] {
      _registration(typeof(TestUserEvent), "digest-a"),
      _registration(typeof(TestUserEvent), "digest-b")
    };
    var options = new TagOptions();
    options.Coalesce("digest-a", c => { });
    options.Coalesce("digest-b", c => c.SlideSeconds = 0);

    await Assert.That(() => TagPolicyValidator.Validate(registrations, options.CoalesceBindings))
      .ThrowsNothing();
  }

  #endregion

  #region Hosted-service startup seam

  [Test]
  public async Task StartAsync_SysViolationInRegistrations_FailsHostStartAsync() {
    // The hosted validator is the "fails startup" seam: a throw from StartAsync aborts
    // host.RunAsync(). The registration source is injectable so this test does not have to
    // pollute the process-global MessageTagRegistry.
    var validator = new TagPolicyStartupValidator(
      new TagOptions(),
      () => [_registration(typeof(TestUserEvent), "sys-mine")]);

    await Assert.That(async () => await validator.StartAsync(CancellationToken.None))
      .Throws<TagPolicyConfigurationException>();
  }

  [Test]
  public async Task StartAsync_CleanRegistrations_CompletesAsync() {
    var validator = new TagPolicyStartupValidator(
      new TagOptions(),
      () => [_registration(typeof(TestUserEvent), "audit")]);

    await Assert.That(async () => {
      await validator.StartAsync(CancellationToken.None);
      await validator.StopAsync(CancellationToken.None);
    }).ThrowsNothing();
  }

  [Test]
  public async Task AddWhizbang_RegistersTheStartupValidatorAsync() {
    var services = new ServiceCollection();

    services.AddWhizbang(null);

    var registered = services.Any(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(TagPolicyStartupValidator));
    await Assert.That(registered).IsTrue()
      .Because("tag policy invariants must be validated at host start, not discovered as silent policy drift");
  }

  [Test]
  public async Task AddWhizbang_CalledTwice_RegistersTheStartupValidatorOnceAsync() {
    var services = new ServiceCollection();

    services.AddWhizbang(null);
    services.AddWhizbang(null);

    var count = services.Count(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(TagPolicyStartupValidator));
    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_SecondCall_MergesCoalesceBindingsIntoTheFirstTagOptionsAsync() {
    // AddWhizbang can run more than once (module composition); hooks already merge across
    // calls — coalesce bindings must too, last-wins per tag, into the SAME TagOptions
    // singleton the first call registered.
    var services = new ServiceCollection();
    services.AddWhizbang(o => o.Tags.Coalesce("digest-a", c => c.SlideSeconds = 5));

    services.AddWhizbang(o => {
      o.Tags.Coalesce("digest-a", c => c.SlideSeconds = 30);
      o.Tags.Coalesce("digest-b", c => c.MaxBatchCount = 50);
    });

    var tagOptions = (TagOptions)services.Single(d => d.ServiceType == typeof(TagOptions)).ImplementationInstance!;
    await Assert.That(tagOptions.CoalesceBindings.Count).IsEqualTo(2);
    await Assert.That(tagOptions.CoalesceBindings["digest-a"].SlideSeconds).IsEqualTo(30)
      .Because("last-wins per tag applies across AddWhizbang calls too");
    await Assert.That(tagOptions.CoalesceBindings["digest-b"].MaxBatchCount).IsEqualTo(50);
  }

  #endregion

  #region Helpers

  private static Dictionary<string, CoalescePolicyOptions> _noBindings() => [];

  private static MessageTagRegistration _registration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new SignalTagAttribute { Tag = tag }
  };

  // Deliberately internal test types: they stand in for application-declared message types.
  internal sealed record TestUserEvent;

  internal sealed record TestOtherUserEvent;

  #endregion
}
