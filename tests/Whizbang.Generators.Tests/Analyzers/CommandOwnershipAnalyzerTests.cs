using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="CommandOwnershipAnalyzer"/> (WHIZ151, Error): two DIFFERENT receptor
/// classes registering inbox handlers for the same COMMAND type inside one compilation is a
/// modeling error — one service owns a command type (N instances of the same service are fine;
/// the runtime topology-drift check covers the cross-service case build-time analysis cannot
/// see).
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/CommandOwnershipAnalyzer.cs</code-under-test>
public class CommandOwnershipAnalyzerTests {
  [Test]
  public async Task TwoReceptorClasses_SameCommand_ReportsWhiz151OnEachAsync() {
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;

      public class CreateOrderReceptor : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      public class DuplicateCreateOrderReceptor : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    var whiz151 = diagnostics.Where(d => d.Id == "WHIZ151").ToList();
    await Assert.That(whiz151.Count).IsEqualTo(2)
      .Because("both claimants are flagged — either could be the one that must move");
    await Assert.That(whiz151[0].Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
      .Because("duplicate command ownership is a modeling error, not a style suggestion");
    await Assert.That(whiz151[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("CreateOrder");
  }

  [Test]
  public async Task SingleReceptor_PerCommand_IsSilentAsync() {
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;
      public record CancelOrder(string Name) : ICommand;

      public class CreateOrderReceptor : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      public class CancelOrderReceptor : IReceptor<CancelOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CancelOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }

  [Test]
  public async Task TwoReceptorClasses_SameEvent_IsSilentAsync() {
    // Events fan out to many receptors by design — ownership applies to COMMANDS only.
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Events;

      public record OrderCreated(string Name) : IEvent;

      public class AuditReceptor : IReceptor<OrderCreated> {
        public System.Threading.Tasks.ValueTask HandleAsync(OrderCreated message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      public class NotifyReceptor : IReceptor<OrderCreated> {
        public System.Threading.Tasks.ValueTask HandleAsync(OrderCreated message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }

  [Test]
  public async Task LifecycleOnlyReceptors_SameCommand_AreSilentAsync() {
    // [FireAt(PreInbox*/PostInbox*/...)] receptors are lifecycle hooks, not inbox handlers —
    // many services legitimately observe the same command's lifecycle.
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;

      [FireAt(LifecycleStage.PreInboxInline)]
      public class AuditHook : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      [FireAt(LifecycleStage.PostInboxDetached)]
      public class MetricsHook : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }

  [Test]
  public async Task SyncAndAsyncReceptors_DifferentClasses_SameCommand_ReportsAsync() {
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;

      public class AsyncReceptor : IReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      public class SyncReceptor : ISyncReceptor<CreateOrder> {
        public void Handle(CreateOrder message) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151").Count()).IsEqualTo(2)
      .Because("sync and async receptor surfaces are the same inbox-handler claim");
  }

  [Test]
  public async Task OneClass_BothReceptorSurfaces_SameCommand_IsSilentAsync() {
    // A single class implementing both IReceptor<T> and ISyncReceptor<T> is ONE registration
    // unit — no duplicate claim.
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;

      public class CreateOrderReceptor : IReceptor<CreateOrder>, ISyncReceptor<CreateOrder> {
        public System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
        public void Handle(CreateOrder message) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }

  [Test]
  public async Task FrameworkSystemCommands_Duplicates_AreSilentAsync() {
    // Framework system commands (whizbang.core.commands.system subtree) classify as
    // MessageKind.System — broadcast/run-control traffic EVERY service handles; ownership
    // does not apply.
    const string source = """
      using Whizbang.Core;

      namespace Whizbang.Core.Commands.System {
        public record RebuildPerspective(string Name) : ICommand;
      }

      namespace ConsumerApp {
        using Whizbang.Core.Commands.System;

        public class FirstSystemReceptor : IReceptor<RebuildPerspective> {
          public global::System.Threading.Tasks.ValueTask HandleAsync(RebuildPerspective message, global::System.Threading.CancellationToken cancellationToken = default)
            => global::System.Threading.Tasks.ValueTask.CompletedTask;
        }

        public class SecondSystemReceptor : IReceptor<RebuildPerspective> {
          public global::System.Threading.Tasks.ValueTask HandleAsync(RebuildPerspective message, global::System.Threading.CancellationToken cancellationToken = default)
            => global::System.Threading.Tasks.ValueTask.CompletedTask;
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty()
      .Because("System-kind messages are exempt — every service subscribes to the broadcast inbox");
  }

  [Test]
  public async Task NamespaceConventionCommand_WithoutInterface_Duplicates_ReportsAsync() {
    // Kind detection parity with the receptor registry generator: a type in a ".Commands."
    // namespace classifies as Command even without the ICommand marker.
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Billing.Commands;

      public record IssueInvoice(string Name);

      public class FirstReceptor : IReceptor<IssueInvoice> {
        public System.Threading.Tasks.ValueTask HandleAsync(IssueInvoice message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }

      public class SecondReceptor : IReceptor<IssueInvoice> {
        public System.Threading.Tasks.ValueTask HandleAsync(IssueInvoice message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151").Count()).IsEqualTo(2);
  }

  [Test]
  public async Task AbstractReceptorBase_PlusOneConcrete_IsSilentAsync() {
    // Abstract bases are not registration units — only concrete receptor classes claim.
    const string source = """
      using Whizbang.Core;

      namespace ConsumerApp.Orders.Commands;

      public record CreateOrder(string Name) : ICommand;

      public abstract class ReceptorBase : IReceptor<CreateOrder> {
        public abstract System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default);
      }

      public class ConcreteReceptor : ReceptorBase {
        public override System.Threading.Tasks.ValueTask HandleAsync(CreateOrder message, System.Threading.CancellationToken cancellationToken = default)
          => System.Threading.Tasks.ValueTask.CompletedTask;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<CommandOwnershipAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ151")).IsEmpty();
  }
}
