import { Routes } from '@angular/router';
import { ForgotPage } from './pages/forgot';
import { LoginPage } from './pages/login';
import { texts } from './texts';

export const routes: Routes = [
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
];
