import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { Product } from '../store/cart/cart.store';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class ProductService {
  private http = inject(HttpClient);
  private apiUrl = environment.apiUrl;

  // For demo purposes, return mock data
  // In production, this would call the BFF API
  getProducts(): Observable<Product[]> {
    return of([
      {
        productId: 'prod-1',
        name: 'Team Sweatshirt',
        description: 'Premium heavyweight hoodie with embroidered team logo and school colors',
        price: 45.99,
        imageUrl: '/images/sweatshirt.png',
        stock: 75
      },
      {
        productId: 'prod-2',
        name: 'Team T-Shirt',
        description: 'Moisture-wicking performance tee with screen-printed team name',
        price: 24.99,
        imageUrl: '/images/t-shirt.png',
        stock: 120
      },
      {
        productId: 'prod-3',
        name: 'Official Match Soccer Ball',
        description: 'Size 5 competition soccer ball with team logo, FIFA quality approved',
        price: 34.99,
        imageUrl: '/images/soccer-ball.png',
        stock: 45
      },
      {
        productId: 'prod-4',
        name: 'Team Baseball Cap',
        description: 'Adjustable snapback cap with embroidered logo and moisture-wicking band',
        price: 19.99,
        imageUrl: '/images/baseball-cap.png',
        stock: 90
      },
      {
        productId: 'prod-5',
        name: 'Foam #1 Finger',
        description: 'Giant foam finger in school colors - perfect for game day!',
        price: 12.99,
        imageUrl: '/images/foam-finger.png',
        stock: 150
      },
      {
        productId: 'prod-6',
        name: 'Team Golf Umbrella',
        description: '62-inch vented canopy umbrella with team colors and logo',
        price: 29.99,
        imageUrl: '/images/umbrella.png',
        stock: 35
      },
      {
        productId: 'prod-7',
        name: 'Portable Stadium Seat',
        description: 'Padded bleacher cushion with backrest in team colors',
        price: 32.99,
        imageUrl: '/images/bleacher-seat.png',
        stock: 60
      },
      {
        productId: 'prod-8',
        name: 'Team Beanie',
        description: 'Warm knit beanie with embroidered team logo for cold game days',
        price: 16.99,
        imageUrl: '/images/beanie.png',
        stock: 85
      },
      {
        productId: 'prod-9',
        name: 'Team Scarf',
        description: 'Knitted supporter scarf with team name and colors - 60 inches long',
        price: 22.99,
        imageUrl: '/images/scarf.png',
        stock: 70
      },
      {
        productId: 'prod-10',
        name: 'Water Bottle',
        description: '32oz insulated stainless steel bottle with team logo',
        price: 27.99,
        imageUrl: '/images/bottle.png',
        stock: 100
      },
      {
        productId: 'prod-11',
        name: 'Team Pennant',
        description: 'Felt pennant flag 12x30 inches - perfect for bedroom or locker decoration',
        price: 14.99,
        imageUrl: '/images/pennant.png',
        stock: 125
      },
      {
        productId: 'prod-12',
        name: 'Team Drawstring Bag',
        description: 'Lightweight cinch sack with zippered pocket - great for gym or practice',
        price: 18.99,
        imageUrl: '/images/drawstring-bag.png',
        stock: 95
      }
    ]);
  }

  getProduct(productId: string): Observable<Product | undefined> {
    return of(undefined); // Would fetch from API
  }
}
