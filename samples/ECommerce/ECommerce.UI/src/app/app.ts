import { Component, OnInit } from '@angular/core';
import {
  ClarityIcons,
  userIcon,
  angleIcon,
  shoppingCartIcon,
  checkCircleIcon,
  plusIcon,
  minusIcon,
  trashIcon,
  checkIcon,
  refreshIcon,
  dotCircleIcon,
  shoppingBagIcon,
  dollarIcon,
  clockIcon,
  timesCircleIcon
} from '@cds/core/icon';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  standalone: false,
  styleUrl: './app.scss'
})
export class App implements OnInit {
  ngOnInit(): void {
    // Register Clarity icons
    ClarityIcons.addIcons(
      userIcon,
      angleIcon,
      shoppingCartIcon,
      checkCircleIcon,
      plusIcon,
      minusIcon,
      trashIcon,
      checkIcon,
      refreshIcon,
      dotCircleIcon,
      shoppingBagIcon,
      dollarIcon,
      clockIcon,
      timesCircleIcon
    );
  }
}
