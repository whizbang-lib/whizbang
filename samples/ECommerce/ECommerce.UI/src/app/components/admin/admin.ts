import { Component, OnInit, OnDestroy, inject } from '@angular/core';
import { Store } from '@ngrx/store';
import { Observable, combineLatest } from 'rxjs';
import { map } from 'rxjs/operators';
import * as OrderActions from '../../store/orders/order.actions';
import * as OrderSelectors from '../../store/orders/order.selectors';
import { Order } from '../../store/orders/order.actions';
import { ProductService } from '../../services/product.service';
import { Product } from '../../store/cart/cart.store';

interface DashboardStats {
  totalOrders: number;
  totalRevenue: number;
  pendingOrders: number;
  completedOrders: number;
  failedOrders: number;
}

@Component({
  selector: 'app-admin',
  templateUrl: './admin.html',
  standalone: false,
  styleUrl: './admin.scss'
})
export class Admin implements OnInit, OnDestroy {
  private store = inject(Store);
  private productService = inject(ProductService);

  orders$: Observable<Order[]>;
  loading$: Observable<boolean>;
  signalRConnected$: Observable<boolean>;
  stats$: Observable<DashboardStats>;
  products: Product[] = [];

  selectedView: 'dashboard' | 'orders' | 'inventory' = 'dashboard';

  constructor() {
    this.orders$ = this.store.select(OrderSelectors.selectAllOrders);
    this.loading$ = this.store.select(OrderSelectors.selectOrdersLoading);
    this.signalRConnected$ = this.store.select(OrderSelectors.selectSignalRConnected);

    // Calculate dashboard statistics
    this.stats$ = this.orders$.pipe(
      map(orders => ({
        totalOrders: orders.length,
        totalRevenue: orders.reduce((sum, order) => sum + order.totalAmount, 0),
        pendingOrders: orders.filter(o => !['Completed', 'Cancelled', 'PaymentFailed'].includes(o.status)).length,
        completedOrders: orders.filter(o => o.status === 'Completed').length,
        failedOrders: orders.filter(o => o.status === 'PaymentFailed' || o.status === 'Cancelled').length
      }))
    );
  }

  ngOnInit(): void {
    // Connect to SignalR for real-time updates
    this.store.dispatch(OrderActions.connectSignalR());

    // Load orders
    this.store.dispatch(OrderActions.loadOrders());

    // Load products
    this.productService.getProducts().subscribe(products => {
      this.products = products;
    });
  }

  ngOnDestroy(): void {
    // Disconnect from SignalR
    this.store.dispatch(OrderActions.disconnectSignalR());
  }

  setView(view: 'dashboard' | 'orders' | 'inventory'): void {
    this.selectedView = view;
  }

  refreshData(): void {
    this.store.dispatch(OrderActions.loadOrders());
    this.productService.getProducts().subscribe(products => {
      this.products = products;
    });
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

  getStockBadgeClass(stock: number): string {
    if (stock > 50) return 'badge-success';
    if (stock > 20) return 'badge-warning';
    return 'badge-danger';
  }
}
