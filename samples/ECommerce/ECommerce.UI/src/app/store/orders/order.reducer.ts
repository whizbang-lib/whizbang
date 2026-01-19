import { createReducer, on } from '@ngrx/store';
import * as OrderActions from './order.actions';
import { Order } from './order.actions';

export interface OrderState {
  orders: Order[];
  selectedOrder: Order | null;
  loading: boolean;
  error: string | null;
  signalRConnected: boolean;
  lastUpdate: Date | null;
}

export const initialState: OrderState = {
  orders: [],
  selectedOrder: null,
  loading: false,
  error: null,
  signalRConnected: false,
  lastUpdate: null
};

export const orderReducer = createReducer(
  initialState,

  // Load orders
  on(OrderActions.loadOrders, (state) => ({
    ...state,
    loading: true,
    error: null
  })),
  on(OrderActions.loadOrdersSuccess, (state, { orders }) => ({
    ...state,
    orders,
    loading: false,
    error: null
  })),
  on(OrderActions.loadOrdersFailure, (state, { error }) => ({
    ...state,
    loading: false,
    error
  })),

  // Load single order
  on(OrderActions.loadOrder, (state) => ({
    ...state,
    loading: true,
    error: null
  })),
  on(OrderActions.loadOrderSuccess, (state, { order }) => ({
    ...state,
    selectedOrder: order,
    loading: false,
    error: null
  })),
  on(OrderActions.loadOrderFailure, (state, { error }) => ({
    ...state,
    loading: false,
    error
  })),

  // Create order
  on(OrderActions.createOrder, (state) => ({
    ...state,
    loading: true,
    error: null
  })),
  on(OrderActions.createOrderSuccess, (state) => ({
    ...state,
    loading: false,
    error: null
  })),
  on(OrderActions.createOrderFailure, (state, { error }) => ({
    ...state,
    loading: false,
    error
  })),

  // SignalR real-time updates
  on(OrderActions.orderStatusChanged, (state, { update }) => {
    // Update the order in the list if it exists
    const orders = state.orders.map(order =>
      order.orderId === update.orderId
        ? { ...order, status: update.status, updatedAt: update.timestamp }
        : order
    );

    // Update selected order if it matches
    const selectedOrder = state.selectedOrder?.orderId === update.orderId
      ? { ...state.selectedOrder, status: update.status, updatedAt: update.timestamp }
      : state.selectedOrder;

    return {
      ...state,
      orders,
      selectedOrder,
      lastUpdate: update.timestamp
    };
  }),

  // SignalR connection
  on(OrderActions.signalRConnected, (state) => ({
    ...state,
    signalRConnected: true
  })),
  on(OrderActions.signalRDisconnected, (state) => ({
    ...state,
    signalRConnected: false
  })),
  on(OrderActions.signalRError, (state, { error }) => ({
    ...state,
    error,
    signalRConnected: false
  }))
);
