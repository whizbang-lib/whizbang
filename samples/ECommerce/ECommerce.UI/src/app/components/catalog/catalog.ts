import { Component, OnInit, inject } from '@angular/core';
import { ProductService } from '../../services/product.service';
import { CartStore, Product } from '../../store/cart/cart.store';

@Component({
  selector: 'app-catalog',
  templateUrl: './catalog.html',
  standalone: false,
  styleUrl: './catalog.scss'
})
export class Catalog implements OnInit {
  private productService = inject(ProductService);
  protected cartStore = inject(CartStore);

  products: Product[] = [];
  loading = true;

  ngOnInit(): void {
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
