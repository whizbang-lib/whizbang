import { createFeatureSelector, createSelector } from '@ngrx/store';
import { OrderState } from './order.reducer';

export const selectOrderState = createFeatureSelector<OrderState>('orders');

export const selectAllOrders = createSelector(
  selectOrderState,
  (state) => state.orders
);

export const selectSelectedOrder = createSelector(
  selectOrderState,
  (state) => state.selectedOrder
);

export const selectOrdersLoading = createSelector(
  selectOrderState,
  (state) => state.loading
);

export const selectOrdersError = createSelector(
  selectOrderState,
  (state) => state.error
);

export const selectSignalRConnected = createSelector(
  selectOrderState,
  (state) => state.signalRConnected
);

export const selectLastUpdate = createSelector(
  selectOrderState,
  (state) => state.lastUpdate
);

// Filter orders by status
export const selectOrdersByStatus = (status: string) => createSelector(
  selectAllOrders,
  (orders) => orders.filter(order => order.status === status)
);

// Get order by ID
export const selectOrderById = (orderId: string) => createSelector(
  selectAllOrders,
  (orders) => orders.find(order => order.orderId === orderId)
);
