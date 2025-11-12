import { Injectable, inject } from '@angular/core';
import { Actions, createEffect, ofType } from '@ngrx/effects';
import { of, fromEvent } from 'rxjs';
import { map, catchError, switchMap, tap } from 'rxjs/operators';
import * as OrderActions from './order.actions';
import { OrderService } from '../../services/order.service';
import { SignalRService } from '../../services/signalr.service';

@Injectable()
export class OrderEffects {
  private actions$ = inject(Actions);
  private orderService = inject(OrderService);
  private signalRService = inject(SignalRService);

  // Load orders from API
  loadOrders$ = createEffect(() =>
    this.actions$.pipe(
      ofType(OrderActions.loadOrders),
      switchMap(() =>
        this.orderService.getOrders().pipe(
          map((orders) => OrderActions.loadOrdersSuccess({ orders })),
          catchError((error) =>
            of(OrderActions.loadOrdersFailure({ error: error.message }))
          )
        )
      )
    )
  );

  // Load single order from API
  loadOrder$ = createEffect(() =>
    this.actions$.pipe(
      ofType(OrderActions.loadOrder),
      switchMap(({ orderId }) =>
        this.orderService.getOrder(orderId).pipe(
          map((order) => OrderActions.loadOrderSuccess({ order })),
          catchError((error) =>
            of(OrderActions.loadOrderFailure({ error: error.message }))
          )
        )
      )
    )
  );

  // Create order
  createOrder$ = createEffect(() =>
    this.actions$.pipe(
      ofType(OrderActions.createOrder),
      switchMap(({ customerId, items }) =>
        this.orderService.createOrder(customerId, items).pipe(
          map((correlationId) =>
            OrderActions.createOrderSuccess({ correlationId })
          ),
          catchError((error) =>
            of(OrderActions.createOrderFailure({ error: error.message }))
          )
        )
      )
    )
  );

  // Connect to SignalR
  connectSignalR$ = createEffect(() =>
    this.actions$.pipe(
      ofType(OrderActions.connectSignalR),
      switchMap(() =>
        this.signalRService.connect().pipe(
          map(() => OrderActions.signalRConnected()),
          catchError((error) =>
            of(OrderActions.signalRError({ error: error.message }))
          )
        )
      )
    )
  );

  // Disconnect from SignalR
  disconnectSignalR$ = createEffect(
    () =>
      this.actions$.pipe(
        ofType(OrderActions.disconnectSignalR),
        tap(() => this.signalRService.disconnect())
      ),
    { dispatch: false }
  );

  // Listen for SignalR order status updates
  signalROrderUpdates$ = createEffect(() =>
    this.actions$.pipe(
      ofType(OrderActions.signalRConnected),
      switchMap(() =>
        this.signalRService.onOrderStatusChanged().pipe(
          map((update) => OrderActions.orderStatusChanged({ update })),
          catchError((error) =>
            of(OrderActions.signalRError({ error: error.message }))
          )
        )
      )
    )
  );
}
