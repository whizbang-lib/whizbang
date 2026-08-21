using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

// Contract namespaces for the phase-6 publisher-flip E2E locks. The CLR namespace IS the
// routing input: each maps to a pre-provisioned emulator entity (Config.json) —
// WbTopo.Orders.Commands  → inbox.wbtopo.orders.commands  (single handler: svc-orders)
// WbTopo.Billing.Commands → inbox.wbtopo.billing.commands (multi handler: svc-billing-a/b)
// WbTopo.Fifo.Commands    → inbox.wbtopo.fifo.commands    (session-enabled: svc-fifo)
// WbTopo.Fifo2.Commands   → inbox.wbtopo.fifo2.commands   (session-enabled: svc-fifo)
namespace WbTopo.Orders.Commands {
  /// <summary>Single-handler flip command (handling service: svc-orders).</summary>
  public sealed record PlaceOrder(string Marker);
}

namespace WbTopo.Billing.Commands {
  /// <summary>Multi-handler flip command (handling services: svc-billing-a, svc-billing-b).</summary>
  public sealed record ChargeCard(string Marker);
}

namespace WbTopo.Fifo.Commands {
  /// <summary>Ordering-lock command, namespace A of the cross-namespace interleave pair.</summary>
  public sealed record FifoStep(string Marker, int Sequence);
}

namespace WbTopo.Fifo2.Commands {
  /// <summary>Ordering-lock command, namespace B of the cross-namespace interleave pair.</summary>
  public sealed record Fifo2Step(string Marker, int Sequence);
}

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests {
  /// <summary>
  /// Source-generated JSON context for the phase-6 flip-lock command types, registered
  /// alongside <see cref="TestJsonContext"/> via JsonContextRegistry.
  /// </summary>
  [JsonSerializable(typeof(WbTopo.Orders.Commands.PlaceOrder))]
  [JsonSerializable(typeof(MessageEnvelope<WbTopo.Orders.Commands.PlaceOrder>))]
  [JsonSerializable(typeof(WbTopo.Billing.Commands.ChargeCard))]
  [JsonSerializable(typeof(MessageEnvelope<WbTopo.Billing.Commands.ChargeCard>))]
  [JsonSerializable(typeof(WbTopo.Fifo.Commands.FifoStep))]
  [JsonSerializable(typeof(MessageEnvelope<WbTopo.Fifo.Commands.FifoStep>))]
  [JsonSerializable(typeof(WbTopo.Fifo2.Commands.Fifo2Step))]
  [JsonSerializable(typeof(MessageEnvelope<WbTopo.Fifo2.Commands.Fifo2Step>))]
  [JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  )]
  public partial class WbTopoJsonContext : JsonSerializerContext;

  /// <summary>Registers the flip-lock command types with JsonContextRegistry before Main().</summary>
  internal static class WbTopoJsonContextRegistration {
    [ModuleInitializer]
    internal static void Register() {
      JsonContextRegistry.RegisterContext(WbTopoJsonContext.Default);
      _registerEnvelope<WbTopo.Orders.Commands.PlaceOrder>();
      _registerEnvelope<WbTopo.Billing.Commands.ChargeCard>();
      _registerEnvelope<WbTopo.Fifo.Commands.FifoStep>();
      _registerEnvelope<WbTopo.Fifo2.Commands.Fifo2Step>();
    }

    private static void _registerEnvelope<TPayload>() {
      JsonContextRegistry.RegisterTypeName(
        typeof(MessageEnvelope<TPayload>).AssemblyQualifiedName!,
        typeof(MessageEnvelope<TPayload>),
        WbTopoJsonContext.Default);
      JsonContextRegistry.RegisterTypeName(
        typeof(TPayload).AssemblyQualifiedName!,
        typeof(TPayload),
        WbTopoJsonContext.Default);
    }
  }
}
