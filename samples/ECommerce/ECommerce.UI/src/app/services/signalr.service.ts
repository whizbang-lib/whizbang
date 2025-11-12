import { Injectable } from '@angular/core';
import { Observable, Subject, from } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { OrderStatusUpdate } from '../store/orders/order.actions';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class SignalRService {
  private hubConnection: signalR.HubConnection | null = null;
  private orderStatusSubject = new Subject<OrderStatusUpdate>();

  connect(): Observable<void> {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/order-status`)
      .withAutomaticReconnect()
      .build();

    // Listen for order status changes
    this.hubConnection.on('OrderStatusChanged', (update: OrderStatusUpdate) => {
      this.orderStatusSubject.next(update);
    });

    return from(this.hubConnection.start());
  }

  disconnect(): void {
    if (this.hubConnection) {
      this.hubConnection.stop();
      this.hubConnection = null;
    }
  }

  subscribeToOrder(orderId: string): Observable<void> {
    if (!this.hubConnection) {
      throw new Error('SignalR connection not established');
    }
    return from(this.hubConnection.invoke('SubscribeToOrder', orderId));
  }

  unsubscribeFromOrder(orderId: string): Observable<void> {
    if (!this.hubConnection) {
      throw new Error('SignalR connection not established');
    }
    return from(this.hubConnection.invoke('UnsubscribeFromOrder', orderId));
  }

  onOrderStatusChanged(): Observable<OrderStatusUpdate> {
    return this.orderStatusSubject.asObservable();
  }
}
