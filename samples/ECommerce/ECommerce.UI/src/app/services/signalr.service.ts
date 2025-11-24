import { Injectable } from '@angular/core';
import { Observable, Subject, from } from 'rxjs';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { environment } from '../../environments/environment';

export interface ProductNotification {
  productId: string;
  name: string;
  price: number;
}

export interface InventoryNotification {
  productId: string;
  quantity: number;
}

export interface OrderStatusUpdate {
  orderId: string;
  status: string;
  timestamp: Date;
  message?: string;
  details?: Record<string, any>;
}

@Injectable({
  providedIn: 'root'
})
export class SignalRService {
  // Separate hub connections for different purposes
  private productInventoryHub: HubConnection | null = null;
  private orderStatusHub: HubConnection | null = null;

  private productUpdatedSubject = new Subject<ProductNotification>();
  private inventoryUpdatedSubject = new Subject<InventoryNotification>();
  private orderStatusChangedSubject = new Subject<OrderStatusUpdate>();

  productUpdated$: Observable<ProductNotification> = this.productUpdatedSubject.asObservable();
  inventoryUpdated$: Observable<InventoryNotification> = this.inventoryUpdatedSubject.asObservable();
  orderStatusChanged$: Observable<OrderStatusUpdate> = this.orderStatusChangedSubject.asObservable();

  constructor() {
    this.buildProductInventoryConnection();
    this.buildOrderStatusConnection();
  }

  private buildProductInventoryConnection(): void {
    const productInventoryUrl = environment.apiUrl + '/hubs/product-inventory';
    this.productInventoryHub = new HubConnectionBuilder()
      .withUrl(productInventoryUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    // Register product/inventory event handlers
    this.productInventoryHub.on('ProductUpdated', (notification: ProductNotification) => {
      console.log('SignalR (Product/Inventory): ProductUpdated', notification);
      this.productUpdatedSubject.next(notification);
    });

    this.productInventoryHub.on('InventoryUpdated', (notification: InventoryNotification) => {
      console.log('SignalR (Product/Inventory): InventoryUpdated', notification);
      this.inventoryUpdatedSubject.next(notification);
    });
  }

  private buildOrderStatusConnection(): void {
    const orderStatusUrl = environment.apiUrl + '/hubs/order-status';
    this.orderStatusHub = new HubConnectionBuilder()
      .withUrl(orderStatusUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    // Register order status event handlers
    this.orderStatusHub.on('OrderStatusChanged', (update: OrderStatusUpdate) => {
      console.log('SignalR (Order Status): OrderStatusChanged', update);
      this.orderStatusChangedSubject.next(update);
    });
  }

  async startConnection(): Promise<void> {
    const promises: Promise<void>[] = [];

    // Start product/inventory hub connection
    if (this.productInventoryHub?.state === 'Disconnected') {
      promises.push(
        this.productInventoryHub.start()
          .then(() => console.log('SignalR: Product/Inventory hub connected'))
          .catch(err => {
            console.error('SignalR: Product/Inventory hub connection failed', err);
            throw err;
          })
      );
    }

    // Start order status hub connection
    if (this.orderStatusHub?.state === 'Disconnected') {
      promises.push(
        this.orderStatusHub.start()
          .then(() => console.log('SignalR: Order Status hub connected'))
          .catch(err => {
            console.error('SignalR: Order Status hub connection failed', err);
            throw err;
          })
      );
    }

    await Promise.all(promises);
  }

  async stopConnection(): Promise<void> {
    const promises: Promise<void>[] = [];

    if (this.productInventoryHub) {
      promises.push(
        this.productInventoryHub.stop()
          .then(() => console.log('SignalR: Product/Inventory hub disconnected'))
      );
    }

    if (this.orderStatusHub) {
      promises.push(
        this.orderStatusHub.stop()
          .then(() => console.log('SignalR: Order Status hub disconnected'))
      );
    }

    await Promise.all(promises);
  }

  async subscribeToProduct(productId: string): Promise<void> {
    if (this.productInventoryHub?.state === 'Connected') {
      await this.productInventoryHub.invoke('SubscribeToProduct', productId);
      console.log(`SignalR: Subscribed to product ${productId}`);
    }
  }

  async unsubscribeFromProduct(productId: string): Promise<void> {
    if (this.productInventoryHub?.state === 'Connected') {
      await this.productInventoryHub.invoke('UnsubscribeFromProduct', productId);
      console.log(`SignalR: Unsubscribed from product ${productId}`);
    }
  }

  // Order-related methods (for NgRx effects compatibility)
  connect(): Observable<void> {
    return from(this.startConnection());
  }

  disconnect(): void {
    this.stopConnection();
  }

  onOrderStatusChanged(): Observable<OrderStatusUpdate> {
    return this.orderStatusChanged$;
  }

  async subscribeToOrder(orderId: string): Promise<void> {
    if (this.orderStatusHub?.state === 'Connected') {
      await this.orderStatusHub.invoke('SubscribeToOrder', orderId);
      console.log(`SignalR: Subscribed to order ${orderId}`);
    }
  }

  async unsubscribeFromOrder(orderId: string): Promise<void> {
    if (this.orderStatusHub?.state === 'Connected') {
      await this.orderStatusHub.invoke('UnsubscribeFromOrder', orderId);
      console.log(`SignalR: Unsubscribed from order ${orderId}`);
    }
  }
}
