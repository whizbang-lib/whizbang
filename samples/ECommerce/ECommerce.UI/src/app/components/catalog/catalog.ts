import { Component, OnInit, OnDestroy, inject } from '@angular/core';
import { ProductService } from '../../services/product.service';
import { SignalRService } from '../../services/signalr.service';
import { CartStore, Product } from '../../store/cart/cart.store';
import { Subscription } from 'rxjs';

@Component({
  selector: 'app-catalog',
  templateUrl: './catalog.html',
  standalone: false,
  styleUrl: './catalog.scss'
})
export class Catalog implements OnInit, OnDestroy {
  private productService = inject(ProductService);
  private signalRService = inject(SignalRService);
  protected cartStore = inject(CartStore);

  products: Product[] = [];
  loading = true;
  private subscriptions = new Subscription();

  async ngOnInit(): Promise<void> {
    // Load initial products from BFF API
    this.productService.getProducts().subscribe({
      next: (products) => {
        this.products = products;
        this.loading = false;
      },
      error: (error) => {
        console.error('Error loading products:', error);
        this.loading = false;
      }
    });

    // Start SignalR connection
    try {
      await this.signalRService.startConnection();
    } catch (err) {
      console.error('Failed to connect to SignalR hub', err);
    }

    // Subscribe to ProductUpdated events
    this.subscriptions.add(
      this.signalRService.productUpdated$.subscribe(notification => {
        this.handleProductUpdated(notification);
      })
    );

    // Subscribe to InventoryUpdated events
    this.subscriptions.add(
      this.signalRService.inventoryUpdated$.subscribe(notification => {
        this.handleInventoryUpdated(notification);
      })
    );

    // Subscribe to all visible products for real-time updates
    const currentProducts = this.products;
    for (const product of currentProducts) {
      await this.signalRService.subscribeToProduct(product.productId);
    }
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    this.signalRService.stopConnection();
  }

  private handleProductUpdated(notification: { productId: string; name: string; price: number }): void {
    const index = this.products.findIndex(p => p.productId === notification.productId);
    if (index !== -1) {
      this.products[index] = {
        ...this.products[index],
        name: notification.name,
        price: notification.price
      };
    }
  }

  private handleInventoryUpdated(notification: { productId: string; quantity: number }): void {
    const index = this.products.findIndex(p => p.productId === notification.productId);
    if (index !== -1) {
      this.products[index] = {
        ...this.products[index],
        stock: notification.quantity
      };
    }
  }

  addToCart(product: Product): void {
    this.cartStore.addToCart(product, 1);
  }

  isInCart(productId: string): boolean {
    return this.cartStore.items().some(item => item.product.productId === productId);
  }

  getCartQuantity(productId: string): number {
    const item = this.cartStore.items().find(item => item.product.productId === productId);
    return item?.quantity || 0;
  }
}
