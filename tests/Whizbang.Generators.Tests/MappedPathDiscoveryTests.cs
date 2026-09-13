using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Which models the mapped path cannot materialize, and just as importantly which it can.
/// </summary>
/// <remarks>
/// <para>
/// A model naming a member Entity Framework cannot construct does not fail a query, it fails while
/// the model is built, which stops the service starting. Such a model is routed to the opaque form
/// instead. That routing has a cost, so this has to be exact in both directions: a shape wrongly
/// called unmappable loses its indexing with nobody asking, and a shape wrongly called mappable
/// takes a service down.
/// </para>
/// <para>
/// The shapes here mirror
/// <c>Whizbang.Data.EFCore.Postgres.Tests.CollectionMemberShapeProbeTests</c>, which ran each one
/// against a real model build. That is where the list comes from; this is where it is enforced.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
public class MappedPathDiscoveryTests {
  private static string _modelWith(string members) => $$"""
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;

    namespace TestApp;

    public record Tag(Guid TagId, string Label);

    public class Model {
    {{members}}
    }
    """;

  private static string? _unmappable(string source, string metadataName = "TestApp.Model") {
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var model = compilation.GetTypeByMetadataName(metadataName);
    return global::Whizbang.Generators.Shared.Models.MappedPathDiscovery.UnmappableMember(model);
  }

  private static string? _unmappableMembers(string members) => _unmappable(_modelWith(members));

  /// <summary>A collection the mapped path cannot fill, holding a complex element, is named.</summary>
  [Test]
  [Arguments("IReadOnlyList<Tag>")]
  [Arguments("IReadOnlyCollection<Tag>")]
  [Arguments("ICollection<Tag>")]
  [Arguments("IEnumerable<Tag>")]
  public async Task ACollectionTheMappedPathCannotFillIsReportedAsync(string type) {
    var member = _unmappableMembers($"  public {type} Items {{ get; init; }} = new List<Tag>();");

    await Assert.That(member).IsEqualTo("Items")
      .Because("Entity Framework reports such a member as a navigation complex types do not "
        + "support, so the model has to take the opaque form instead of failing at startup");
  }

  /// <summary>The collections it can fill are left on the mapped path.</summary>
  [Test]
  [Arguments("List<Tag>")]
  [Arguments("IList<Tag>")]
  [Arguments("ImmutableList<Tag>")]
  public async Task ACollectionTheMappedPathCanFillIsNotReportedAsync(string type) {
    var member = _unmappableMembers($"  public {type} Items {{ get; init; }} = null!;");

    await Assert.That(member).IsNull()
      .Because("these build a model today, so routing them to the opaque form would take their "
        + "indexing away for nothing");
  }

  /// <summary>
  /// A collection of primitives is accepted in any shape, because it is not a complex collection.
  /// </summary>
  /// <remarks>
  /// The element type is why this check is not simply "an interface fails". A list of strings is
  /// stored as a value whatever interface declares it.
  /// </remarks>
  [Test]
  public async Task ACollectionOfPrimitivesIsNotReportedAsync() {
    var member = _unmappableMembers("  public IReadOnlyList<string> Items { get; init; } = null!;");

    await Assert.That(member).IsNull();
  }

  /// <summary>
  /// A nested positional record whose constructor takes a collection cannot be constructed.
  /// </summary>
  /// <remarks>
  /// The shape that stopped a consumer's service starting. A collection cannot be bound to a
  /// constructor parameter at all, and a positional record declares no other constructor.
  /// </remarks>
  [Test]
  public async Task ANestedRecordWithACollectionConstructorParameterIsReportedAsync() {
    const string SOURCE = """
      using System;
      using System.Collections.Generic;

      namespace TestApp;

      public record Tag(Guid TagId, string Label);

      public record Turn(Guid TurnId, string Content, IReadOnlyList<Tag>? Tags = null);

      public class Model {
        public List<Turn> Turns { get; init; } = [];
      }
      """;

    var member = _unmappable(SOURCE);

    await Assert.That(member).IsEqualTo("Turns")
      .Because("the element type cannot be constructed, so the collection holding it is where the "
        + "mapped path stops");
  }

  /// <summary>
  /// The same record with the collection outside the constructor is mappable again.
  /// </summary>
  /// <remarks>
  /// The pair to the case above, and the reason the check is about the constructor rather than about
  /// records. It is also the fix to recommend: the shape stays succinct and keeps its indexing.
  /// </remarks>
  [Test]
  public async Task ANestedRecordWithTheCollectionOutsideTheConstructorIsNotReportedAsync() {
    const string SOURCE = """
      using System;
      using System.Collections.Generic;

      namespace TestApp;

      public record Tag(Guid TagId, string Label);

      public record Turn(Guid TurnId, string Content) {
        public List<Tag> Tags { get; init; } = [];
      }

      public class Model {
        public List<Turn> Turns { get; init; } = [];
      }
      """;

    await Assert.That(_unmappable(SOURCE)).IsNull();
  }

  /// <summary>An ordinary model names nothing, which is the common case.</summary>
  [Test]
  public async Task AnOrdinaryModelIsNotReportedAsync() {
    var member = _unmappableMembers("""
        public string Label { get; init; } = string.Empty;
        public int Count { get; init; }
        public DateTimeOffset When { get; init; }
        public List<Tag> Tags { get; init; } = [];
      """);

    await Assert.That(member).IsNull();
  }

  /// <summary>
  /// The consumer shape this was written for, reproduced so the rule is anchored to a real model.
  /// </summary>
  /// <remarks>
  /// A conversation holding turns, each turn a positional record whose last parameter is a read-only
  /// collection of attachments. Both of the conditions this discovery checks are present, and either
  /// one alone is enough to route the model to the opaque form. Kept as a named case because a rule
  /// that drifts away from the shape that motivated it is worse than no rule.
  /// </remarks>
  [Test]
  public async Task TheConsumerShapeThatMotivatedThisIsReportedAsync() {
    const string SOURCE = """
      using System;
      using System.Collections.Generic;

      namespace TestApp;

      public record AttachedFileRef(Guid UploadId, string FileName);

      public record TurnMessage(
        Guid MessageId,
        Guid SenderId,
        bool AssistantMessage,
        string Content,
        DateTimeOffset SentAt,
        Guid? InReplyToMessageId = null,
        string? ActivityTreeId = null,
        IReadOnlyList<AttachedFileRef>? AttachedFiles = null
      );

      public class Model {
        public Guid ConversationId { get; init; }
        public List<TurnMessage> Messages { get; init; } = [];
      }
      """;

    await Assert.That(_unmappable(SOURCE)).IsEqualTo("Messages")
      .Because("the turn cannot be constructed, so the collection holding it is where the mapped "
        + "path stops and the whole document has to take the opaque form");
  }
}
