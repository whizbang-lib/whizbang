using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="MintedCompositeConstructionAnalyzer"/> (WHIZ150): a minted composite type
/// (a <c>CompositeEventBase</c> subclass — built-in family or consumer-authored) constructed
/// outside the mint's internals and outside a registered factory builder gets a Warning; the
/// sanctioned construction sites stay silent.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/MintedCompositeConstructionAnalyzer.cs</code-under-test>
public class MintedCompositeConstructionAnalyzerTests {
  [Test]
  public async Task BuiltInComposite_ConstructedDirectly_ReportsWhiz150Async() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public class RepairService {
        public RedeliveryComposite Build() {
          return new RedeliveryComposite { StreamId = System.Guid.NewGuid() };
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    var whiz150 = diagnostics.Where(d => d.Id == "WHIZ150").ToList();
    await Assert.That(whiz150.Count).IsEqualTo(1)
      .Because("a hand-built redelivery bundle bypasses the mint's grouping/cap/budget invariants");
    await Assert.That(whiz150[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("RedeliveryComposite");
  }

  [Test]
  public async Task ConsumerCompositeSubclass_ConstructedDirectly_ReportsWhiz150Async() {
    const string source = """
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public sealed class OrderBulkImportComposite : CompositeEventBase;

      public class ImportHandler {
        public IMessage Bundle(List<IMessage> events) {
          return new OrderBulkImportComposite { Inner = events };
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    var whiz150 = diagnostics.Where(d => d.Id == "WHIZ150").ToList();
    await Assert.That(whiz150.Count).IsEqualTo(1)
      .Because("the rule covers every CompositeEventBase subclass, not just the built-in families");
    await Assert.That(whiz150[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("OrderBulkImportComposite");
  }

  [Test]
  public async Task Construction_InsideMintRequestBuilder_IsSilentAsync() {
    const string source = """
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public sealed class DigestComposite : CompositeEventBase;

      public class DigestShipper {
        public object Plan(ICompositeFactory factory, System.Collections.Generic.List<IMessage> events) {
          return factory.Create(new CompositeMintRequest<IMessage> {
            Constituents = events,
            GroupKey = CompositeGroupKey.FromKey<IMessage>(_ => "digest"),
            BuildComposite = batch => new DigestComposite { Inner = [.. batch.Constituents] },
          });
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty()
      .Because("CompositeMintRequest<T>.BuildComposite IS the registered factory path — the mint "
             + "invokes it once per split plan");
  }

  [Test]
  public async Task Construction_InsideCoalesceBindingFactory_IsSilentAsync() {
    const string source = """
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Minting;
      using Whizbang.Core.Tags;

      namespace ConsumerApp;

      public class BindingSetup {
        public void Configure(CoalescePolicyOptions binding) {
          binding.CompositeFactory = batch => new CoalescedEventsComposite {
            InnerEventIds = [.. batch.Singles.Select(s => s.MessageId)],
          };
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty()
      .Because("a coalesce binding's CompositeFactory is a registered factory seam — the ship "
             + "worker invokes it through the mint");
  }

  [Test]
  public async Task Construction_InsideMintingNamespace_IsSilentAsync() {
    const string source = """
      using Whizbang.Core.Minting;

      namespace Whizbang.Core.Minting.Internal;

      public class FamilyBuilder {
        public RedeliveryComposite Build() => new RedeliveryComposite();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty()
      .Because("the mint's own internals construct the families it plans");
  }

  [Test]
  public async Task NonCompositeConstruction_IsSilentAsync() {
    const string source = """
      namespace ConsumerApp;

      public record OrderCreated(string Name);

      public class Handler {
        public OrderCreated Make() => new OrderCreated("x");
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150")).IsEmpty();
  }

  [Test]
  public async Task TargetTypedNew_OfMintedComposite_ReportsWhiz150Async() {
    const string source = """
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public class Sneaky {
        public CoalescedEventsComposite Build() {
          CoalescedEventsComposite composite = new() { StreamId = System.Guid.NewGuid() };
          return composite;
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ150").Count()).IsEqualTo(1)
      .Because("target-typed new is the same construction — the operation-based analysis sees both");
  }
}
