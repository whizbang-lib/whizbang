import { Injectable } from '@angular/core';
import { Observable, Subject } from 'rxjs';
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

@Injectable({
  providedIn: 'root'
})
export class SignalRService {
  private hubConnection: HubConnection | null = null;
  private productUpdatedSubject = new Subject<ProductNotification>();
  private inventoryUpdatedSubject = new Subject<InventoryNotification>();

  productUpdated$: Observable<ProductNotification> = this.productUpdatedSubject.asObservable();
  inventoryUpdated$: Observable<InventoryNotification> = this.inventoryUpdatedSubject.asObservable();

  constructor() {
    this.buildConnection();
  }

  private buildConnection(): void {
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(environment.signalRHubUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    // Register event handlers
    this.hubConnection.on('ProductUpdated', (notification: ProductNotification) => {
      console.log('SignalR: ProductUpdated', notification);
      this.productUpdatedSubject.next(notification);
    });

    this.hubConnection.on('InventoryUpdated', (notification: InventoryNotification) => {
      console.log('SignalR: InventoryUpdated', notification);
      this.inventoryUpdatedSubject.next(notification);
    });
  }

  async startConnection(): Promise<void> {
    if (this.hubConnection?.state === 'Disconnected') {
      try {
        await this.hubConnection.start();
        console.log('SignalR: Connection started');
      } catch (err) {
        console.error('SignalR: Connection failed', err);
        throw err;
      }
    }
  }

  async stopConnection(): Promise<void> {
    if (this.hubConnection) {
      await this.hubConnection.stop();
      console.log('SignalR: Connection stopped');
    }
  }

  async subscribeToProduct(productId: string): Promise<void> {
    if (this.hubConnection?.state === 'Connected') {
      await this.hubConnection.invoke('SubscribeToProduct', productId);
      console.log(`SignalR: Subscribed to product ${productId}`);
    }
  }

  async unsubscribeFromProduct(productId: string): Promise<void> {
    if (this.hubConnection?.state === 'Connected') {
      await this.hubConnection.invoke('UnsubscribeFromProduct', productId);
      console.log(`SignalR: Unsubscribed from product ${productId}`);
    }
  }
}
