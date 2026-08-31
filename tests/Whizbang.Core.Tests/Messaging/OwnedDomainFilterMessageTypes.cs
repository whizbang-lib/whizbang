using Whizbang.Core;

// Message contracts for ReceptorInvokerOwnedDomainFilterTests. They live in their own file, and
// in namespaces chosen to exercise the ownership match: Shop.Orders is owned, Shop.Orders.
// Fulfillment is inside it, Shop.OrdersArchive merely shares its prefix, and Billing.Invoices is
// a different service's domain entirely.
namespace Shop.Orders {
  public sealed record PlaceOrder([property: StreamId] Guid OrderId) : ICommand {
    public PlaceOrder() : this(Guid.Empty) { }
  }

  public sealed record OrderPlaced([property: StreamId] Guid OrderId) : IEvent {
    public OrderPlaced() : this(Guid.Empty) { }
  }

  namespace Fulfillment {
    public sealed record OrderShipped([property: StreamId] Guid OrderId) : IEvent {
      public OrderShipped() : this(Guid.Empty) { }
    }
  }
}

namespace Shop.OrdersArchive {
  public sealed record ArchivedOrderRecorded([property: StreamId] Guid OrderId) : IEvent {
    public ArchivedOrderRecorded() : this(Guid.Empty) { }
  }
}

namespace Billing.Invoices {
  public sealed record IssueInvoice([property: StreamId] Guid InvoiceId) : ICommand {
    public IssueInvoice() : this(Guid.Empty) { }
  }

  public sealed record InvoiceIssued([property: StreamId] Guid InvoiceId) : IEvent {
    public InvoiceIssued() : this(Guid.Empty) { }
  }
}
