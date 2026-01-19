import { createAction, props } from '@ngrx/store';

// Order models
export interface Order {
  orderId: string;
  customerId: string;
  status: string;
  totalAmount: number;
  createdAt: Date;
  updatedAt: Date;
  items: OrderItem[];
}

export interface OrderItem {
  productId: string;
  productName: string;
  quantity: number;
  price: number;
}

export interface OrderStatusUpdate {
  orderId: string;
  status: string;
  timestamp: Date;
  message?: string;
  details?: Record<string, any>;
}

// Load orders
export const loadOrders = createAction('[Orders] Load Orders');
export const loadOrdersSuccess = createAction(
  '[Orders] Load Orders Success',
  props<{ orders: Order[] }>()
);
export const loadOrdersFailure = createAction(
  '[Orders] Load Orders Failure',
  props<{ error: string }>()
);

// Load single order
export const loadOrder = createAction(
  '[Orders] Load Order',
  props<{ orderId: string }>()
);
export const loadOrderSuccess = createAction(
  '[Orders] Load Order Success',
  props<{ order: Order }>()
);
export const loadOrderFailure = createAction(
  '[Orders] Load Order Failure',
  props<{ error: string }>()
);

// Create order
export const createOrder = createAction(
  '[Orders] Create Order',
  props<{ customerId: string; items: OrderItem[] }>()
);
export const createOrderSuccess = createAction(
  '[Orders] Create Order Success',
  props<{ correlationId: string }>()
);
export const createOrderFailure = createAction(
  '[Orders] Create Order Failure',
  props<{ error: string }>()
);

// SignalR real-time updates
export const orderStatusChanged = createAction(
  '[SignalR] Order Status Changed',
  props<{ update: OrderStatusUpdate }>()
);

// SignalR connection
export const connectSignalR = createAction('[SignalR] Connect');
export const disconnectSignalR = createAction('[SignalR] Disconnect');
export const signalRConnected = createAction('[SignalR] Connected');
export const signalRDisconnected = createAction('[SignalR] Disconnected');
export const signalRError = createAction(
  '[SignalR] Error',
  props<{ error: string }>()
);
