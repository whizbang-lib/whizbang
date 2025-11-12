import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Store } from '@ngrx/store';
import { CartStore } from '../../store/cart/cart.store';
import { createOrder } from '../../store/orders/order.actions';

@Component({
  selector: 'app-cart',
  templateUrl: './cart.html',
  standalone: false,
  styleUrl: './cart.scss'
})
export class Cart {
  protected cartStore = inject(CartStore);
  private store = inject(Store);
  private router = inject(Router);

  updateQuantity(productId: string, quantity: number): void {
    this.cartStore.updateQuantity(productId, quantity);
  }

  removeItem(productId: string): void {
    this.cartStore.removeFromCart(productId);
  }

  checkout(): void {
    if (this.cartStore.isEmpty()) {
      return;
    }

    // Convert cart items to order items
    const orderItems = this.cartStore.items().map(item => ({
      productId: item.product.productId,
      productName: item.product.name,
      quantity: item.quantity,
      price: item.product.price
    }));

    // Dispatch create order action
    this.store.dispatch(createOrder({
      customerId: 'customer-123', // In real app, get from auth
      items: orderItems
    }));

    // Clear cart
    this.cartStore.clearCart();

    // Navigate to orders page
    this.router.navigate(['/orders']);
  }

  continueShopping(): void {
    this.router.navigate(['/catalog']);
  }
}
