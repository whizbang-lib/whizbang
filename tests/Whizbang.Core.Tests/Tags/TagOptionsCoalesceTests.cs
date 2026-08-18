using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for the <see cref="TagOptions.Coalesce"/> binding surface — tags classify,
/// policies bind. A coalesce binding attaches a sliding-window batching policy to a tag
/// string; message types never declare batching themselves.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TagOptionsCoalesceTests {
  [Test]
  public async Task CoalesceBindings_IsEmptyByDefaultAsync() {
    var options = new TagOptions();

    await Assert.That(options.CoalesceBindings.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Coalesce_RegistersBindingForTagAsync() {
    var options = new TagOptions();

    options.Coalesce("audit", c => c.SlideSeconds = 30);

    await Assert.That(options.CoalesceBindings.ContainsKey("audit")).IsTrue();
    await Assert.That(options.CoalesceBindings["audit"].SlideSeconds).IsEqualTo(30);
    await Assert.That(options.CoalesceBindings["audit"].MaxDelaySeconds).IsEqualTo(120)
      .Because("unconfigured knobs keep the CoalescePolicyOptions defaults");
  }

  [Test]
  public async Task Coalesce_LastWinsPerTag_ReplacesNotMergesAsync() {
    // Last-registration-wins is the shipped-default-override mechanism: the framework registers
    // built-in bindings first, and a host binding for the same tag REPLACES it entirely.
    // Replacement (not merge) is load-bearing — the first binding's non-default knobs must not
    // bleed into the second.
    var options = new TagOptions();
    options.Coalesce("audit", c => {
      c.SlideSeconds = 5;
      c.MaxDelaySeconds = 999;
    });

    options.Coalesce("audit", c => c.SlideSeconds = 30);

    await Assert.That(options.CoalesceBindings.Count).IsEqualTo(1);
    await Assert.That(options.CoalesceBindings["audit"].SlideSeconds).IsEqualTo(30);
    await Assert.That(options.CoalesceBindings["audit"].MaxDelaySeconds).IsEqualTo(120)
      .Because("a later binding for the same tag replaces the earlier one entirely — MaxDelaySeconds=999 must not survive");
  }

  [Test]
  public async Task Coalesce_MultipleTags_BindIndependentlyAsync() {
    var options = new TagOptions();

    options.Coalesce("audit", c => c.SlideSeconds = 30);
    options.Coalesce("telemetry-digest", c => c.MaxBatchCount = 50);

    await Assert.That(options.CoalesceBindings.Count).IsEqualTo(2);
    await Assert.That(options.CoalesceBindings["audit"].SlideSeconds).IsEqualTo(30);
    await Assert.That(options.CoalesceBindings["audit"].MaxBatchCount).IsEqualTo(500);
    await Assert.That(options.CoalesceBindings["telemetry-digest"].MaxBatchCount).IsEqualTo(50);
    await Assert.That(options.CoalesceBindings["telemetry-digest"].SlideSeconds).IsEqualTo(15);
  }

  [Test]
  public async Task Coalesce_ReturnsSameInstanceForChainingAsync() {
    var options = new TagOptions();

    var result = options.Coalesce("audit", c => c.SlideSeconds = 30);

    await Assert.That(result).IsEqualTo(options);
  }

  [Test]
  public async Task Coalesce_NullTag_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.Coalesce(null!, c => { })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Coalesce_WhitespaceTag_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.Coalesce("   ", c => { })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Coalesce_NullConfigure_ThrowsAsync() {
    var options = new TagOptions();

    await Assert.That(() => options.Coalesce("audit", null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UseCoalesceBindingDefault_DoesNotReplaceAnExistingHostBindingAsync() {
    // The built-in registration path (EnableAudit → sys-audit defaults) uses add-if-absent
    // semantics so it behaves as "registered FIRST" regardless of the order the host called
    // AddWhizbang / AddSystemEvents in: a host binding always survives.
    var options = new TagOptions();
    options.Coalesce(SystemTags.AUDIT, c => c.SlideSeconds = 45);

    options.UseCoalesceBindingDefault(SystemTags.AUDIT, new CoalescePolicyOptions { SlideSeconds = 15 });

    await Assert.That(options.CoalesceBindings[SystemTags.AUDIT].SlideSeconds).IsEqualTo(45)
      .Because("built-ins ship as defaults, never as locks — the host binding must win");
  }

  [Test]
  public async Task UseCoalesceBindingDefault_AddsWhenAbsentAsync() {
    var options = new TagOptions();

    options.UseCoalesceBindingDefault(SystemTags.AUDIT, new CoalescePolicyOptions { SlideSeconds = 15 });

    await Assert.That(options.CoalesceBindings.ContainsKey(SystemTags.AUDIT)).IsTrue();
    await Assert.That(options.CoalesceBindings[SystemTags.AUDIT].SlideSeconds).IsEqualTo(15);
  }

  [Test]
  public async Task Coalesce_AfterDefault_ReplacesTheDefaultAsync() {
    // The other override direction: built-in default registered first, host binding second.
    var options = new TagOptions();
    options.UseCoalesceBindingDefault(SystemTags.AUDIT, new CoalescePolicyOptions { SlideSeconds = 15 });

    options.Coalesce(SystemTags.AUDIT, c => {
      c.SlideSeconds = 30;
      c.MaxDelaySeconds = 300;
    });

    await Assert.That(options.CoalesceBindings[SystemTags.AUDIT].SlideSeconds).IsEqualTo(30);
    await Assert.That(options.CoalesceBindings[SystemTags.AUDIT].MaxDelaySeconds).IsEqualTo(300);
  }
}
