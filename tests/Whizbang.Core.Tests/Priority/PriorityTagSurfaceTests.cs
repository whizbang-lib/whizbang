using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// The tag and classification surfaces (priority step 2) are sugar over the hooks: a producer declares a
/// priority for every message carrying a tag, and a consumer classifies by namespace, by type, or by a rule
/// over the received message. Each is an ordinary hook that runs before the framework defaults, so the default
/// keeps what the sugar declared and reads an undeclared number as standard.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaring-with-tags</docs>
[Category("Unit")]
public class PriorityTagSurfaceTests {

  private sealed record _importRow(string Id) : IEvent;
  private sealed record _permissionRevoked(string User) : IEvent;
  private sealed record _plain(string Id) : IEvent;

  /// <summary>A registry that tags one type, the way the generated registry tags attributed message types.</summary>
  private sealed class _oneTagRegistry(Type taggedType, string tag) : IMessageTagRegistry {
    private readonly MessageTagRegistration _registration = new() {
      MessageType = taggedType,
      AttributeType = typeof(MessageTagAttribute),
      Tag = tag,
      PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
      AttributeFactory = () => throw new NotSupportedException("no attribute instance in this test"),
    };
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) => messageType == taggedType ? [_registration] : [];
    public IEnumerable<MessageTagRegistration> GetAllTags() => [_registration];
  }

  private static readonly Lazy<bool> _registered = new(() => {
    MessageTagRegistry.Register(new _oneTagRegistry(typeof(_importRow), "bulk-import"));
    return true;
  });

  private static MessageEnvelope<T> _envelope<T>(T payload, int declared = WorkPriority.UNDECLARED) => new() {
    MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Local, HandlerName = "H" },
    Priority = declared,
  };

  private static PriorityDeclarationContext _declaration<T>(T payload, int declared = WorkPriority.UNDECLARED) where T : notnull =>
    new(_envelope(payload, declared), TypeNameFormatter.AssemblyQualifiedName(typeof(T)), null, false, WorkPriority.UNDECLARED, declared);

  private static PriorityReceiveContext _receive(string messageTypeName, int declared) =>
    new(declared, _envelope("x", declared), messageTypeName);

  // ---- producer: declare by tag ---------------------------------------------------------------

  [Test]
  public async Task DeclarePriority_ForATag_DeclaresEveryMessageCarryingItAsync() {
    _ = _registered.Value;
    var tags = new TagOptions().DeclarePriority("bulk-import", WorkPriority.BACKGROUND);
    var hook = new TagDeclaredPriorityProducerHook(tags);

    await Assert.That(hook.DeclarePriority(_declaration(new _importRow("r")))).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a tag classifies; binding a priority to it declares every message type that carries it, whatever context it is dispatched from");
    await Assert.That(hook.DeclarePriority(_declaration(new _plain("p")))).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("a type without a bound tag is left for the framework default to derive from context");
  }

  [Test]
  public async Task DeclarePriority_KeepsAnEarlierExplicitDeclarationAsync() {
    _ = _registered.Value;
    var tags = new TagOptions().DeclarePriority("bulk-import", WorkPriority.BACKGROUND);
    var hook = new TagDeclaredPriorityProducerHook(tags);

    await Assert.That(hook.DeclarePriority(_declaration(new _importRow("r"), declared: 20))).IsEqualTo(20)
      .Because("an explicit declaration made earlier in the chain is the more specific word");
  }

  [Test]
  public async Task DeclarePriority_LastBindingPerTagWinsAsync() {
    var tags = new TagOptions().DeclarePriority("bulk-import", WorkPriority.BACKGROUND).DeclarePriority("bulk-import", WorkPriority.STANDARD);
    await Assert.That(tags.PriorityDeclarations["bulk-import"]).IsEqualTo(WorkPriority.STANDARD)
      .Because("the same last-wins rule the other tag bindings use, so a host binding replaces a built-in one");
  }

  [Test]
  public async Task AddWhizbang_CalledTwice_MergesTheTagDeclarations_LastWinsPerTagAsync() {
    var services = new ServiceCollection();
    _ = services.AddWhizbang(o => o.Tags.DeclarePriority("bulk-import", WorkPriority.BACKGROUND).DeclarePriority("shared", WorkPriority.STANDARD));
    _ = services.AddWhizbang(o => o.Tags.DeclarePriority("shared", WorkPriority.INTERACTIVE));
    await using var provider = services.BuildServiceProvider();

    var tags = provider.GetRequiredService<TagOptions>();

    await Assert.That(tags.PriorityDeclarations["bulk-import"]).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a declaration from the first call survives a second call that does not mention it");
    await Assert.That(tags.PriorityDeclarations["shared"]).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("last wins per tag across AddWhizbang calls, the rule every other tag binding follows");
  }

  // ---- consumer: classify by namespace, type, or rule -----------------------------------------

  [Test]
  public async Task ClassifyNamespace_LowersEverythingInTheNamespace_AndLeavesTheRestAsync() {
    var options = new PriorityOptions().ClassifyNamespace("Contracts.Job", WorkPriority.BACKGROUND);
    var hook = new PriorityClassificationReceiveHook(options);

    await Assert.That(hook.Classify(_receive("Contracts.Job.RowAddedEvent, Contracts", WorkPriority.INTERACTIVE))).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("everything from the job domain is background for this service, whatever the producer said");
    await Assert.That(hook.Classify(_receive("Contracts.Order.PlacedEvent, Contracts", WorkPriority.INTERACTIVE))).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("classification is positive: a rule that does not match leaves the declared number in place");
    await Assert.That(hook.Classify(_receive("Contracts.JobHistory.X, Contracts", WorkPriority.INTERACTIVE))).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("a namespace matches on a segment boundary, not on a string prefix");
  }

  [Test]
  public async Task ClassifyType_RaisesOneType_AndTheMostSpecificRuleWinsAsync() {
    var options = new PriorityOptions()
      .ClassifyNamespace("Whizbang.Core.Tests.Priority", WorkPriority.BACKGROUND)
      .ClassifyType<_permissionRevoked>(WorkPriority.INTERACTIVE);
    var hook = new PriorityClassificationReceiveHook(options);

    await Assert.That(hook.Classify(_receive(TypeNameFormatter.AssemblyQualifiedName(typeof(_permissionRevoked)), WorkPriority.STANDARD))).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("a consumer may raise by declared policy; the type rule is more specific than the namespace rule that would lower it");
    await Assert.That(hook.Classify(_receive(TypeNameFormatter.AssemblyQualifiedName(typeof(_plain)), WorkPriority.STANDARD))).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task Classify_ARule_DecidesByContent_AndNullKeepsTheDeclaredNumberAsync() {
    var options = new PriorityOptions().Classify(ctx => ctx.MessageTypeName.Contains("Import", StringComparison.Ordinal) ? WorkPriority.BACKGROUND : null);
    var hook = new PriorityClassificationReceiveHook(options);

    await Assert.That(hook.Classify(_receive("Contracts.Job.ImportRowEvent, Contracts", WorkPriority.STANDARD))).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That(hook.Classify(_receive("Contracts.Job.RowEvent, Contracts", 42))).IsEqualTo(42)
      .Because("a rule that returns null has no opinion");
  }

  // ---- registration ---------------------------------------------------------------------------

  [Test]
  public async Task AddWhizbangPriority_RegistersTheSugarHooksAheadOfTheDefaultsAsync() {
    _ = _registered.Value;
    var services = new ServiceCollection();
    services.AddSingleton(new TagOptions().DeclarePriority("bulk-import", WorkPriority.BACKGROUND));
    services.AddSingleton(new PriorityOptions().ClassifyNamespace("Contracts.Job", WorkPriority.BACKGROUND));
    services.AddWhizbangPriority();
    using var sp = services.BuildServiceProvider();
    var chain = sp.GetRequiredService<PriorityHookChain>();

    await Assert.That(chain.DeclarePriority(_declaration(new _importRow("r")))).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the tag hook runs before the context default, which keeps the declaration");
    await Assert.That(chain.Classify(_receive("Contracts.Job.RowAddedEvent, Contracts", WorkPriority.UNDECLARED))).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the classification hook runs before the accept-declared default");
    await Assert.That(chain.Classify(_receive("Contracts.Order.X, Contracts", WorkPriority.UNDECLARED))).IsEqualTo(WorkPriority.STANDARD)
      .Because("with no rule the default still reads undeclared as standard");
  }
}
