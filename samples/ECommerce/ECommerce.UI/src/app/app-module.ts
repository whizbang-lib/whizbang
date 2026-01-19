import { NgModule, provideBrowserGlobalErrorListeners } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { ClarityModule } from '@clr/angular';
import { StoreModule } from '@ngrx/store';
import { EffectsModule } from '@ngrx/effects';
import { StoreDevtoolsModule } from '@ngrx/store-devtools';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { orderReducer } from './store/orders/order.reducer';
import { OrderEffects } from './store/orders/order.effects';
import { environment } from '../environments/environment';
import { Catalog } from './components/catalog/catalog';
import { Cart } from './components/cart/cart';
import { Orders } from './components/orders/orders';
import { Admin } from './components/admin/admin';

@NgModule({
  declarations: [
    App,
    Catalog,
    Cart,
    Orders,
    Admin
  ],
  imports: [
    BrowserModule,
    BrowserAnimationsModule,
    ClarityModule,
    AppRoutingModule,

    // NgRx Store
    StoreModule.forRoot({
      orders: orderReducer
    }),

    // NgRx Effects
    EffectsModule.forRoot([OrderEffects]),

    // NgRx DevTools (only in development)
    StoreDevtoolsModule.instrument({
      maxAge: 25,
      logOnly: environment.production,
      connectInZone: true
    })
  ],
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient()
  ],
  bootstrap: [App]
})
export class AppModule { }
