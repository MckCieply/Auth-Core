import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { App } from '../app';
import { OrgEntry } from '../account-api';
import { adminMe, privateMe, settle, signedInAs } from '../../testing/helpers';
import { authInterceptor, wantsToken } from './auth.interceptor';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ACME = adminMe.org_id;
const PRIVATE = privateMe.org_id;
const GAMMA = '66666666-6666-6666-6666-666666666666';

function entries(current: string): OrgEntry[] {
  return [
    { id: ACME, name: 'Acme', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: current === ACME },
    { id: PRIVATE, name: 'private', kind: 'personal', role: { id: 'r2', name: 'private' }, current: current === PRIVATE },
  ];
}

describe('AuthService: more than one company (spec 0015)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
    navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });

  afterEach(() => ctrl.verify());

  describe('the list', () => {
    it('is empty before anything was asked', () => {
      expect(auth.orgs()).toEqual([]);
      expect(auth.orgChanges()).toBe(0);
    });

    it('is loaded after a sign-in', async () => {
      const done = auth.login('admin@example.test', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await done).toEqual({ ok: true });
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(ACME) });
      await settle();
      expect(auth.orgs()).toEqual(entries(ACME));
    });

    it('is loaded when the session is resumed at the start', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await started;
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(ACME) });
      await settle();
      expect(auth.orgs()).toEqual(entries(ACME));
    });

    it('keeps the last list when an answer cannot be read', async () => {
      await signedInAs(auth, ctrl);
      const first = auth.loadOrgs();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(ACME) });
      await first;
      const second = auth.loadOrgs();
      ctrl.expectOne('/auth/orgs').flush('oops', status(500));
      await second;
      expect(auth.orgs()).toEqual(entries(ACME));
    });

    it('is emptied when the session ends, and an answer that arrives after that is not kept', async () => {
      await signedInAs(auth, ctrl);
      const late = auth.loadOrgs();
      const request = ctrl.expectOne('/auth/orgs');
      auth.dropSession();
      request.flush({ orgs: entries(ACME) });
      await late;
      expect(auth.orgs()).toEqual([]);
    });
  });

  describe('switchTo', () => {
    it('200: the same session keeps the new token, asks /auth/me and the list again and tells the page to reload', async () => {
      await signedInAs(auth, ctrl);
      const session = auth.session;
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ org_id: PRIVATE });
      expect(request.request.headers.has('Authorization')).toBe(false);
      request.flush({ access_token: 'tok-2' });
      await settle();
      expect(auth.token()).toBe('tok-2');
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      expect(await done).toBe('switched');
      expect(auth.session).toBe(session);
      expect(auth.me()).toEqual(privateMe);
      expect(auth.orgs()).toEqual(entries(PRIVATE));
      expect(auth.orgChanges()).toBe(1);
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('200: the tab that switched is not told "in another tab"', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      ctrl.expectOne('/auth/switch').flush({ access_token: 'tok-2' });
      await settle();
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      await done;
      expect(auth.notice()).toBeNull();
    });

    it('404: the list is asked again, the answer is "gone" and the session is as it was', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      ctrl.expectOne('/auth/switch').flush({ error: 'not_found' }, status(404));
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: [entries(ACME)[0]] });
      expect(await done).toBe('gone');
      expect(auth.token()).toBe('tok');
      expect(auth.me()).toEqual(adminMe);
      expect(auth.orgs()).toEqual([entries(ACME)[0]]);
      expect(auth.orgChanges()).toBe(0);
    });

    it('401: the session is dropped, /login is opened and no refresh is sent', async () => {
      await signedInAs(auth, ctrl);
      const session = auth.session;
      const done = auth.switchTo(PRIVATE);
      ctrl.expectOne('/auth/switch').flush({ error: 'invalid_grant' }, status(401));
      expect(await done).toBe('signed-out');
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
      expect(auth.session).toBe(session + 1);
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
      ctrl.expectNone('/auth/refresh');
    });

    it('a refresh that is on its way is waited for: /auth/switch goes out after its answer, and its token is not the one kept', async () => {
      await signedInAs(auth, ctrl);
      const refreshing = auth.refresh();
      const refresh = ctrl.expectOne('/auth/refresh');
      const done = auth.switchTo(PRIVATE);
      await settle();
      ctrl.expectNone('/auth/switch');
      refresh.flush({ access_token: 'tok-refreshed' });
      await refreshing;
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      ctrl.expectOne('/auth/switch').flush({ access_token: 'tok-switched' });
      await settle();
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      expect(await done).toBe('switched');
      expect(auth.token()).toBe('tok-switched');
      expect(auth.notice()).toBeNull();
    });

    it('a refresh that is asked while a switch is on its way joins it: one /auth/switch, no /auth/refresh, and the switched token stays', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      const refreshed = auth.refresh();
      await settle();
      ctrl.expectNone('/auth/refresh');
      request.flush({ access_token: 'tok-switched' });
      expect(await refreshed).toBe('ok');
      await settle();
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      expect(await done).toBe('switched');
      expect(auth.token()).toBe('tok-switched');
      ctrl.expectNone('/auth/refresh');
    });

    it('a refresh that is asked while a switch is on its way and finds the switch gone runs its own refresh afterwards', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      const refreshed = auth.refresh();
      await settle();
      ctrl.expectNone('/auth/refresh');
      request.flush({ error: 'not_found' }, status(404));
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: [entries(ACME)[0]] });
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-refreshed' });
      expect(await refreshed).toBe('ok');
      expect(await done).toBe('gone');
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(auth.token()).toBe('tok-refreshed');
    });

    it('a refresh that joined a switch which ends 401 gets "rejected": the session is over and no /auth/refresh is sent', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      const refreshed = auth.refresh();
      await settle();
      request.flush({ error: 'invalid_grant' }, status(401));
      expect(await refreshed).toBe('rejected');
      expect(await done).toBe('signed-out');
      expect(auth.token()).toBeNull();
      ctrl.expectNone('/auth/refresh');
    });

    it('a refresh that joined a switch which ends 503 runs its own refresh afterwards', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      const refreshed = auth.refresh();
      await settle();
      ctrl.expectNone('/auth/refresh');
      request.flush(null, status(503));
      expect(await done).toBe('failed');
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-refreshed' });
      expect(await refreshed).toBe('ok');
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(auth.token()).toBe('tok-refreshed');
    });

    it('a refresh asked after a new sign-in, while the old session switch is still away, is not rejected: it runs its own refresh', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      auth.dropSession(); // signed out, and signed in again: a refresh is asked for the new session
      const refreshed = auth.refresh();
      await settle();
      ctrl.expectNone('/auth/refresh');
      request.flush({ access_token: 'tok-old-session' });
      expect(await done).toBe('signed-out');
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-new-session' });
      expect(await refreshed).toBe('ok');
      expect(auth.token()).toBe('tok-new-session');
    });

    it('a second switch asked while the first is away waits for it: its /auth/switch goes out after the first answer', async () => {
      await signedInAs(auth, ctrl);
      const first = auth.switchTo(PRIVATE);
      const firstRequest = ctrl.expectOne('/auth/switch');
      expect(firstRequest.request.body).toEqual({ org_id: PRIVATE });
      const second = auth.switchTo(ACME);
      await settle();
      ctrl.expectNone('/auth/switch');
      firstRequest.flush({ access_token: 'tok-2' });
      await settle();
      const secondRequest = ctrl.expectOne('/auth/switch');
      expect(secondRequest.request.body).toEqual({ org_id: ACME });
      secondRequest.flush({ access_token: 'tok-3' });
      for (let round = 0; round < 4; round++) {
        await settle();
        ctrl.match('/auth/me').forEach((me) => me.flush(adminMe));
        ctrl.match('/auth/orgs').forEach((list) => list.flush({ orgs: entries(ACME) }));
      }
      expect(await first).toBe('switched');
      expect(await second).toBe('switched');
      expect(auth.token()).toBe('tok-3');
    });

    it('503: nothing changes, and the answer says it failed', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      ctrl.expectOne('/auth/switch').flush(null, status(503));
      expect(await done).toBe('failed');
      expect(auth.token()).toBe('tok');
      expect(auth.orgChanges()).toBe(0);
    });

    it('a session that ended while the call was away is signed out, and its token is not kept', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.switchTo(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      auth.dropSession();
      request.flush({ access_token: 'tok-2' });
      expect(await done).toBe('signed-out');
      expect(auth.token()).toBeNull();
    });
  });

  describe('the interceptor', () => {
    it('gives /auth/orgs the token and /auth/switch none', () => {
      expect(wantsToken('/auth/orgs', 'https://app.example')).toBe(true);
      expect(wantsToken('/auth/switch', 'https://app.example')).toBe(false);
    });
  });

  describe('a refresh (the cookie is shared with the other tabs)', () => {
    async function refreshInto(token: string): Promise<void> {
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: token });
      await done;
      await settle();
    }

    it('that brings another org_id: the page reloads, the list is asked again and the bar says so', async () => {
      await signedInAs(auth, ctrl);
      await refreshInto('tok-2');
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      await settle();
      expect(auth.orgChanges()).toBe(1);
      expect(auth.me()).toEqual(privateMe);
      expect(auth.orgs()).toEqual(entries(PRIVATE));
      expect(auth.notice()).toBe('switched');
      expect(auth.noticeName()).toBeNull();
    });

    it('that brings another company: the bar names it', async () => {
      await signedInAs(auth, ctrl, privateMe);
      await refreshInto('tok-2');
      ctrl.expectOne('/auth/me').flush(adminMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(ACME) });
      await settle();
      expect(auth.notice()).toBe('switched');
      expect(auth.noticeName()).toBe('Acme');
    });

    it('with the same org_id: nothing is shown, the page keeps its notes and the list is not asked again', async () => {
      await signedInAs(auth, ctrl);
      await refreshInto('tok-2');
      ctrl.expectOne('/auth/me').flush(adminMe);
      await settle();
      ctrl.expectNone('/auth/orgs');
      expect(auth.orgChanges()).toBe(0);
      expect(auth.notice()).toBeNull();
    });

    it('whose /auth/me cannot be read: nothing is shown and the refresh was fine', async () => {
      await signedInAs(auth, ctrl);
      await refreshInto('tok-2');
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      await settle();
      expect(auth.token()).toBe('tok-2');
      expect(auth.orgChanges()).toBe(0);
      expect(auth.notice()).toBeNull();
    });

    it('while nobody is known yet (the first token of a tab): /auth/me is not asked for it', async () => {
      await refreshInto('tok-1');
      ctrl.expectNone('/auth/me');
      expect(auth.notice()).toBeNull();
    });

    it('that is read while this tab switches: the switch is not "another tab"', async () => {
      await signedInAs(auth, ctrl);
      const refreshing = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-2' });
      await refreshing;
      await settle();
      const late = ctrl.expectOne('/auth/me');
      const switching = auth.switchTo(PRIVATE);
      ctrl.expectOne('/auth/switch').flush({ access_token: 'tok-3' });
      await settle();
      late.flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/me').flush(privateMe);
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
      expect(await switching).toBe('switched');
      await settle();
      expect(auth.orgChanges()).toBe(1);
      expect(auth.notice()).toBeNull();
    });

    it('does not make the refresh wait for /auth/me', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-2' });
      expect(await done).toBe('ok');
      ctrl.expectOne('/auth/me');
      ctrl.expectNone('/auth/orgs');
    });
  });

  describe('the notice bar', () => {
    it('says "Switched to Acme in another tab." and, for the personal space, "Private"', async () => {
      const fixture = TestBed.createComponent(App);
      fixture.detectChanges();
      const text = (): string | undefined | null =>
        (fixture.nativeElement as HTMLElement).querySelector('[data-testid="notice-text"]')?.textContent;
      auth.showSwitchedNotice('Acme');
      await settle(fixture);
      expect(text()).toBe('Switched to Acme in another tab.');
      auth.showSwitchedNotice(null);
      await settle(fixture);
      expect(text()).toBe('Switched to Private in another tab.');
    });
  });

  describe('createCompany (spec 0016)', () => {
    const created = { id: GAMMA, name: 'Gamma', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: false };

    it('createCompany_creates_then_switches_and_ends_in_the_new_company', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      const create = ctrl.expectOne('/auth/orgs');
      expect(create.request.method).toBe('POST');
      expect(create.request.body).toEqual({ name: 'Gamma' });
      expect(create.request.headers.get('Authorization')).toBe('Bearer tok');
      create.flush(created, status(201));
      await settle();
      const sw = ctrl.expectOne('/auth/switch');
      expect(sw.request.body).toEqual({ org_id: GAMMA });
      sw.flush({ access_token: 'tok-2' });
      await settle();
      ctrl.expectOne('/auth/me').flush({ ...adminMe, org_id: GAMMA, org_name: 'Gamma' });
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: [...entries(ACME), { ...created, current: true }] });
      expect(await done).toBe('created');
      expect(auth.token()).toBe('tok-2');
      expect(auth.me()?.org_name).toBe('Gamma');
      expect(auth.orgChanges()).toBe(1);
    });

    it('createCompany_on_409_returns_limit_and_does_not_switch', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush({ error: 'company_limit_reached' }, status(409));
      expect(await done).toBe('limit');
      ctrl.expectNone('/auth/switch');
      expect(auth.token()).toBe('tok');
    });

    it('createCompany_on_org_creation_disabled_returns_disabled_and_does_not_switch', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush({ error: 'org_creation_disabled' }, status(403));
      expect(await done).toBe('disabled');
      ctrl.expectNone('/auth/switch');
    });

    it('createCompany_on_permissions_changed_returns_changed', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      // The interceptor treats it as for any company call: one refresh, the person asked for again, the request sent once more.
      ctrl.expectOne('/auth/orgs').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-r' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      ctrl.expectOne('/auth/orgs').flush({ error: 'permissions_changed' }, status(403));
      expect(await done).toBe('changed');
      ctrl.expectNone('/auth/switch');
    });

    it('createCompany_on_400_returns_invalid', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('');
      ctrl.expectOne('/auth/orgs').flush({ error: 'invalid_request' }, status(400));
      expect(await done).toBe('invalid');
    });

    it('createCompany_on_any_other_answer_returns_failed', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush('oops', status(500));
      expect(await done).toBe('failed');
    });

    it('createCompany_when_the_switch_says_404_returns_gone_and_when_it_fails_returns_switch_failed', async () => {
      await signedInAs(auth, ctrl);
      const first = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush(created, status(201));
      await settle();
      ctrl.expectOne('/auth/switch').flush({ error: 'not_found' }, status(404));
      await settle();
      ctrl.expectOne('/auth/orgs').flush({ orgs: entries(ACME) });
      expect(await first).toBe('gone');
      const second = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush(created, status(201));
      await settle();
      ctrl.expectOne('/auth/switch').flush('oops', status(503));
      expect(await second).toBe('switch-failed');
    });

    it('createCompany_when_the_switch_ends_the_session_returns_failed_and_opens_login', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.createCompany('Gamma');
      ctrl.expectOne('/auth/orgs').flush(created, status(201));
      await settle();
      ctrl.expectOne('/auth/switch').flush({ error: 'invalid_grant' }, status(401));
      expect(await done).toBe('failed');
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });
  });
});
