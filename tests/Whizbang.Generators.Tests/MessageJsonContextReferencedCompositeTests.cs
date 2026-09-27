using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #915: a consumer service that handles the INNER events of a composite declared in a shared
/// contracts assembly never names the composite type itself, yet the publisher sends the composite on
/// the wire and the consumer stores it as an ordinary inbox row before fanning it out at dispatch.
/// Serializing that row needs JSON metadata for the composite, and deserializing its inner list needs
/// the inner events registered as <c>IMessage</c> derived types. The consumer's own generated context
/// must therefore carry both — it is the one context guaranteed to be registered before the consumer's
/// JSON options are built, whatever order the contract assembly's own module initializer runs in.
/// </summary>
/// <remarks>
/// Each test compiles a real contracts assembly to an in-memory image and references it from the
/// consumer compilation, so the composite and the inner events are genuinely metadata symbols, the
/// exact shape the generator meets in a deployed consumer. The consumer's own syntax never declares
/// either one.
/// </remarks>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextReferencedCompositeTests {

  private const string CONTRACTS_SOURCE = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Minting;

    namespace SharedContracts.Items;

    public sealed record ItemCreated(Guid ItemId, string Name) : IEvent;
    public sealed record ItemRenamed(Guid ItemId, string Name) : IEvent;
    public sealed record ArchiveItem(Guid ItemId) : ICommand;

    public sealed class ItemBulkImportComposite : CompositeEventBase;

    public static class Batches {
      public sealed class NestedItemComposite : CompositeEventBase;
      internal sealed class HiddenNestedComposite : CompositeEventBase;
    }

    internal static class InternalHolder {
      public sealed class UnreachableComposite : CompositeEventBase;
    }

    internal sealed class InternalComposite : CompositeEventBase;
    public abstract class AbstractItemComposite : CompositeEventBase;
    public sealed class GenericItemComposite<T> : CompositeEventBase;
    public sealed record NotAComposite(Guid ItemId);
    public abstract record AbstractItemEvent(Guid ItemId) : IEvent;
    public sealed record GenericItemEvent<T>(T Value) : IEvent;
    """;

  private const string CONSUMER_SOURCE = """
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using SharedContracts.Items;

    namespace ConsumerService.Items;

    internal sealed class ItemCreatedReceptor : IReceptor<ItemCreated> {
      public ValueTask HandleAsync(ItemCreated message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ConsumerHandlesOnlyInnerEvent_EmitsMetadataForReferencedCompositeAsync() {
    var (context, _, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(context).Contains("MessageEnvelope<global::SharedContracts.Items.ItemBulkImportComposite>")
      .Because("the consumer stores the composite as an inbox row before fan-out; without envelope metadata "
             + "in its own context the row cannot be serialized and the composite is lost at the transport edge");
    await Assert.That(context).Contains("typeof(global::SharedContracts.Items.ItemBulkImportComposite)")
      .Because("the composite payload itself needs a JsonTypeInfo the consumer's options can resolve");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReferencedComposite_RegisteredAsMessageButNotEventAsync() {
    var (_, initializer, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(initializer).Contains(
        "RegisterDerivedType<global::Whizbang.Core.IMessage, global::SharedContracts.Items.ItemBulkImportComposite>")
      .Because("a composite travels as an IMessage, so the consumer must be able to resolve it polymorphically");
    await Assert.That(initializer).DoesNotContain(
        "RegisterDerivedType<global::Whizbang.Core.IEvent, global::SharedContracts.Items.ItemBulkImportComposite>")
      .Because("a composite is wire-only and must never be treated as a persisted event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorForReferencedEvent_RegistersInnerEventAsMessageAsync() {
    var (context, initializer, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(initializer).Contains(
        "RegisterDerivedType<global::Whizbang.Core.IMessage, global::SharedContracts.Items.ItemCreated>")
      .Because("a composite's inner list is an IMessage list: an inner event the consumer handles must be "
             + "resolvable by its discriminator, or the whole composite fails to deserialize");
    await Assert.That(initializer).Contains(
        "RegisterDerivedType<global::Whizbang.Core.IEvent, global::SharedContracts.Items.ItemCreated>");
    await Assert.That(context).Contains("typeof(global::SharedContracts.Items.ItemCreated)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReferencedEventNobodyHandles_NotAddedAsync() {
    var (_, initializer, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(initializer).DoesNotContain("global::SharedContracts.Items.ItemRenamed")
      .Because("only message types this assembly consumes are pulled in from a referenced assembly; "
             + "an unrelated event would only bloat every consumer's context");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorForReferencedCommandWithResponse_RegistersCommandAsync() {
    const string consumer = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using SharedContracts.Items;

      namespace ConsumerService.Items;

      public sealed class ArchiveItemReceptor : IReceptor<ArchiveItem, ItemRenamed> {
        public ValueTask<ItemRenamed> HandleAsync(ArchiveItem message, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new ItemRenamed(message.ItemId, "archived"));
      }

      public sealed class ItemRenamedSyncReceptor : ISyncReceptor<ItemRenamed> {
        public void Handle(ItemRenamed message) { }
      }
      """;

    var (_, initializer, errors) = _runAgainstContracts(consumer);

    await Assert.That(errors).IsEmpty();
    await Assert.That(initializer).Contains(
        "RegisterDerivedType<global::Whizbang.Core.ICommand, global::SharedContracts.Items.ArchiveItem>");
    await Assert.That(initializer).Contains(
        "RegisterDerivedType<global::Whizbang.Core.IEvent, global::SharedContracts.Items.ItemRenamed>")
      .Because("a synchronous receptor consumes its message type exactly as an asynchronous one does");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReferencedComposites_OnlyConcretePublicNonGenericOnesAsync() {
    var (context, _, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(context).Contains("typeof(global::SharedContracts.Items.Batches.NestedItemComposite)")
      .Because("a public composite nested in a public type is as reachable as a top-level one");
    await Assert.That(context).DoesNotContain("HiddenNestedComposite");
    await Assert.That(context).DoesNotContain("UnreachableComposite")
      .Because("a public type inside an internal one cannot be named from the consumer's generated code");
    await Assert.That(context).DoesNotContain("InternalComposite");
    await Assert.That(context).DoesNotContain("AbstractItemComposite");
    await Assert.That(context).DoesNotContain("GenericItemComposite");
    await Assert.That(context).DoesNotContain("NotAComposite");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_FrameworkComposites_NotDuplicatedIntoConsumerAsync() {
    var (context, _, _) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(context).DoesNotContain("RedeliveryComposite")
      .Because("the framework's own composites are registered by the framework's context, which every "
             + "consumer loads first; copying them into every consumer would only duplicate metadata");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReferencedCompositeAndInnerEvent_GeneratedCodeCompilesAsync() {
    var (_, _, errors) = _runAgainstContracts(CONSUMER_SOURCE);

    await Assert.That(errors).IsEmpty()
      .Because("metadata emitted for types from another assembly must compile in the consumer");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReceptorsForUnqualifiedReferencedTypes_AddNothingAsync() {
    // Only a concrete, non-generic command or event from a referenced contracts assembly is pulled in:
    // an abstract or constructed generic type cannot be materialized from generated code, a plain
    // record is not a message, and the framework's own events are registered by the framework.
    const string consumer = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using SharedContracts.Items;

      namespace ConsumerService.Items;

      internal sealed class AbstractReceptor : IReceptor<AbstractItemEvent> {
        public ValueTask HandleAsync(AbstractItemEvent message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }

      internal sealed class GenericReceptor : IReceptor<GenericItemEvent<int>> {
        public ValueTask HandleAsync(GenericItemEvent<int> message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }

      internal sealed class PlainRecordReceptor : IReceptor<NotAComposite> {
        public ValueTask HandleAsync(NotAComposite message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }

      internal sealed class FrameworkEventReceptor : IReceptor<Whizbang.Core.SystemEvents.EventAudited> {
        public ValueTask HandleAsync(Whizbang.Core.SystemEvents.EventAudited message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }
      """;

    var (_, initializer, errors) = _runAgainstContracts(consumer);

    await Assert.That(errors).IsEmpty();
    await Assert.That(initializer).DoesNotContain("AbstractItemEvent");
    await Assert.That(initializer).DoesNotContain("GenericItemEvent");
    await Assert.That(initializer).DoesNotContain("NotAComposite");
    await Assert.That(initializer).DoesNotContain("EventAudited")
      .Because("the framework's context registers the framework's own events");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CompilationWithoutFramework_DiscoversNoReferencedCompositesAsync() {
    // A compilation that does not reference Whizbang.Core has no composite interface to look for.
    const string source = """
      namespace PlainLibrary;
      public sealed class Nothing;
      """;
    var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
    var compilation = CSharpCompilation.Create(
        assemblyName: "PlainLibrary",
        syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
        references: trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path)),
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var result = CSharpGeneratorDriver.Create(new MessageJsonContextGenerator()).RunGenerators(compilation).GetRunResult();

    await Assert.That(result.Results.Select(r => r.Exception).Where(e => e is not null)).IsEmpty()
      .Because("discovery must decline quietly, not throw, when there is no framework to discover against");
    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs") ?? string.Empty)
      .DoesNotContain("Composite");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoReferencedContracts_OutputHasNoContractTypesAsync() {
    // Control: the same consumer shape with no contracts reference adds nothing from elsewhere.
    const string consumer = """
      using System;
      using Whizbang.Core;

      namespace ConsumerService.Local;

      public sealed record LocalEvent(Guid Id) : IEvent;
      """;

    var (context, _, errors) = _run(consumer, additionalReferences: []);

    await Assert.That(errors).IsEmpty();
    await Assert.That(context).DoesNotContain("SharedContracts");
    await Assert.That(context).DoesNotContain("RedeliveryComposite");
  }

  // ------------------------------------------------------------------------------------------

  [RequiresAssemblyFiles()]
  private static (string Context, string Initializer, ImmutableArray<Diagnostic> Errors) _runAgainstContracts(string consumerSource)
    => _run(consumerSource, [_emitContracts()]);

  [RequiresAssemblyFiles()]
  private static (string Context, string Initializer, ImmutableArray<Diagnostic> Errors) _run(
      string consumerSource, IReadOnlyList<MetadataReference> additionalReferences) {
    var compilation = CSharpCompilation.Create(
        assemblyName: "ConsumerService",
        syntaxTrees: [CSharpSyntaxTree.ParseText(consumerSource)],
        references: [.. _frameworkReferences(), .. additionalReferences],
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    // The JSON context references the WhizbangId context its sibling generator emits, so both run.
    var driver = CSharpGeneratorDriver.Create(new MessageJsonContextGenerator(), new WhizbangIdGenerator());
    var ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
    var result = ran.GetRunResult();
    var failure = result.Results.Select(r => r.Exception).FirstOrDefault(e => e is not null);
    if (failure is not null) {
      throw new InvalidOperationException("MessageJsonContextGenerator threw instead of generating.", failure);
    }

    var context = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs") ?? string.Empty;
    var initializer = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs") ?? string.Empty;
    var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
    return (context, initializer, errors);
  }

  [RequiresAssemblyFiles()]
  private static PortableExecutableReference _emitContracts() {
    var contracts = CSharpCompilation.Create(
        assemblyName: "SharedContracts",
        syntaxTrees: [CSharpSyntaxTree.ParseText(CONTRACTS_SOURCE)],
        references: _frameworkReferences(),
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    using var image = new MemoryStream();
    var emitted = contracts.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The contracts fixture does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    return MetadataReference.CreateFromImage(image.ToArray());
  }

  /// <summary>The whole shared framework plus Whizbang.Core, so both fixtures and the generated code compile.</summary>
  [RequiresAssemblyFiles()]
  private static List<MetadataReference> _frameworkReferences() {
    var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
    var references = trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      .Where(File.Exists)
      .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
      .ToList();
    references.Add(MetadataReference.CreateFromFile(typeof(Whizbang.Core.IMessage).Assembly.Location));
    return references;
  }
}
