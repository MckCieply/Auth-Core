import { inject } from '@angular/core';
import { Params, Router, Routes } from '@angular/router';
import { DEFAULT_RETURN_URL, authGuard } from './auth/auth.guard';
import { ForgotPage } from './pages/forgot';
import { InvitePage } from './pages/invite';
import { LoginPage } from './pages/login';
import { NotesPage } from './pages/notes';
import { ResetPage } from './pages/reset';
import { VerifyPage } from './pages/verify';
import { texts } from './texts';

/** To the notes, keeping the query except `token`: a mail token that reached a route the app has not (a wrong FrontendUrls) goes no further. */
function toNotes({ queryParams }: { queryParams: Params }) {
  const query = Object.fromEntries(Object.entries(queryParams).filter(([name]) => name !== 'token'));
  return inject(Router).createUrlTree([DEFAULT_RETURN_URL], { queryParams: query });
}

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: toNotes },
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
  { path: 'reset', component: ResetPage, title: texts.reset.title },
  { path: 'verify', component: VerifyPage, title: texts.verify.title },
  { path: 'invite', component: InvitePage, title: texts.invite.title },
  { path: 'notes', component: NotesPage, canActivate: [authGuard], title: texts.notes.title },
  { path: '**', redirectTo: toNotes },
];
