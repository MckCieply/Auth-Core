import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { ForgotPage } from './pages/forgot';
import { InvitePage } from './pages/invite';
import { LoginPage } from './pages/login';
import { NotesPage } from './pages/notes';
import { ResetPage } from './pages/reset';
import { VerifyPage } from './pages/verify';
import { texts } from './texts';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'notes' },
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
  { path: 'reset', component: ResetPage, title: texts.reset.title },
  { path: 'verify', component: VerifyPage, title: texts.verify.title },
  { path: 'invite', component: InvitePage, title: texts.invite.title },
  { path: 'notes', component: NotesPage, canActivate: [authGuard], title: texts.notes.title },
  { path: '**', redirectTo: 'notes' },
];
