import { Component, OnInit, OnDestroy, inject } from '@angular/core';
import { Store } from '@ngrx/store';
import { Observable } from 'rxjs';
import * as OrderActions from '../../store/orders/order.actions';
import * as OrderSelectors from '../../store/orders/order.selectors';
import { Order } from '../../store/orders/order.actions';

@Component({
  selector: 'app-orders',
  templateUrl: './orders.html',
  standalone: false,
  styleUrl: './orders.scss'
})
export class Orders implements OnInit, OnDestroy {
  private store = inject(Store);

  orders$: Observable<Order[]>;
  loading$: Observable<boolean>;
  error$: Observable<string | null>;
  signalRConnected$: Observable<boolean>;
  lastUpdate$: Observable<Date | null>;
  selectedOrder$: Observable<Order | null>;

  selectedOrderId: string | null = null;

  constructor() {
    this.orders$ = this.store.select(OrderSelectors.selectAllOrders);
    this.loading$ = this.store.select(OrderSelectors.selectOrdersLoading);
    this.error$ = this.store.select(OrderSelectors.selectOrdersError);
    this.signalRConnected$ = this.store.select(OrderSelectors.selectSignalRConnected);
    this.lastUpdate$ = this.store.select(OrderSelectors.selectLastUpdate);
    this.selectedOrder$ = this.store.select(OrderSelectors.selectSelectedOrder);
  }

  ngOnInit(): void {
    // Connect to SignalR for real-time updates
    this.store.dispatch(OrderActions.connectSignalR());

    // Load orders
    this.store.dispatch(OrderActions.loadOrders());
  }

  ngOnDestroy(): void {
    // Disconnect from SignalR
    this.store.dispatch(OrderActions.disconnectSignalR());
  }

  viewOrderDetails(orderId: string): void {
    this.selectedOrderId = orderId;
    this.store.dispatch(OrderActions.loadOrder({ orderId }));
  }

  closeDetails(): void {
    this.selectedOrderId = null;
  }

  refreshOrders(): void {
    this.store.dispatch(OrderActions.loadOrders());
  }

  getStatusBadgeClass(status: string): string {
    const statusMap: Record<string, string> = {
      'Created': 'badge-info',
      'InventoryReserved': 'badge-info',
      'PaymentProcessed': 'badge-success',
      'PaymentFailed': 'badge-danger',
      'ShipmentCreated': 'badge-success',
      'Completed': 'badge-success',
      'Cancelled': 'badge-danger'
    };
    return statusMap[status] || 'badge-secondary';
  }

  getStatusLabel(status: string): string {
    const labelMap: Record<string, string> = {
      'Created': 'Order Created',
      'InventoryReserved': 'Inventory Reserved',
      'PaymentProcessed': 'Payment Processed',
      'PaymentFailed': 'Payment Failed',
      'ShipmentCreated': 'Shipped',
      'Completed': 'Completed',
      'Cancelled': 'Cancelled'
    };
    return labelMap[status] || status;
  }
}
