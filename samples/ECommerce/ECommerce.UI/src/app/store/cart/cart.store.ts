import { computed } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';

export interface Product {
  productId: string;
  name: string;
  description: string;
  price: number;
  imageUrl: string;
  stock: number;
}

export interface CartItem {
  product: Product;
  quantity: number;
}

interface CartState {
  items: CartItem[];
}

const initialState: CartState = {
  items: []
};

export const CartStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed(({ items }) => ({
    totalItems: computed(() =>
      items().reduce((sum, item) => sum + item.quantity, 0)
    ),
    totalPrice: computed(() =>
      items().reduce((sum, item) => sum + (item.product.price * item.quantity), 0)
    ),
    isEmpty: computed(() => items().length === 0)
  })),
  withMethods((store) => ({
    addToCart(product: Product, quantity: number = 1) {
      const existingItem = store.items().find(
        item => item.product.productId === product.productId
      );

      if (existingItem) {
        // Update quantity of existing item
        patchState(store, {
          items: store.items().map(item =>
            item.product.productId === product.productId
              ? { ...item, quantity: item.quantity + quantity }
              : item
          )
        });
      } else {
        // Add new item
        patchState(store, {
          items: [...store.items(), { product, quantity }]
        });
      }
    },

    removeFromCart(productId: string) {
      patchState(store, {
        items: store.items().filter(item => item.product.productId !== productId)
      });
    },

    updateQuantity(productId: string, quantity: number) {
      if (quantity <= 0) {
        this.removeFromCart(productId);
        return;
      }

      patchState(store, {
        items: store.items().map(item =>
          item.product.productId === productId
            ? { ...item, quantity }
            : item
        )
      });
    },

    clearCart() {
      patchState(store, { items: [] });
    }
  }))
);
