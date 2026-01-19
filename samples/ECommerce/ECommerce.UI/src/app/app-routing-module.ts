import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Catalog } from './components/catalog/catalog';
import { Cart } from './components/cart/cart';
import { Orders } from './components/orders/orders';
import { Admin } from './components/admin/admin';

const routes: Routes = [
  { path: '', redirectTo: '/catalog', pathMatch: 'full' },
  { path: 'catalog', component: Catalog },
  { path: 'cart', component: Cart },
  { path: 'orders', component: Orders },
  { path: 'admin', component: Admin }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
