import { OrderState } from './orders/order.reducer';

/**
 * Root application state
 */
export interface AppState {
  orders: OrderState;
}
