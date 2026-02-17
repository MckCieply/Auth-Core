import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, holdToken, pageText, settle, signedInAs, submitForm, typeInto, viewerMe } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });

// A note's time, as the service sends it (ISO 8601, UTC): the n-th day after the epoch.
const day = (n: number): string => new Date(n * 86_400_000).toISOString();

const newer = { id: 'n2', text: 'second note', author_sub: 'u1', created_at: day(2) };
const older = { id: 'n1', text: 'first note', author_sub: 'u1', created_at: day(1) };

async function open(me: Me = adminMe, notes: object = [newer, older]) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush(notes);
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

function textOf(fixture: { nativeElement: unknown }, selector: string): string | undefined {
  return (fixture.nativeElement as HTMLElement).querySelector(selector)?.textContent?.trim();
}

describe('NotesPage', () => {
  it('shows the email, company and role from /auth/me, and "Sign out"', async () => {
    const { fixture } = await open();
    expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
    expect(textOf(fixture, '[data-testid="me-company"]')).toBe('Acme');
    expect(textOf(fixture, '[data-testid="me-role"]')).toBe('admin');
    expect(textOf(fixture, '[data-testid="sign-out"]')).toBe('Sign out');
  });

  it('lists the notes in the order the service sends them, newest first', async () => {
    const { fixture } = await open();
    const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);
    expect(items).toEqual(['second note', 'first note']);
  });

  it('says so when there are no notes', async () => {
    const { fixture } = await open(adminMe, []);
    expect(pageText(fixture)).toContain('No notes yet.');
  });

  describe('the form to add a note', () => {
    it('is shown when /auth/me lists notes:write', async () => {
      const { fixture } = await open(adminMe);
      expect(has(fixture, '#text')).toBe(true);
      expect(pageText(fixture)).toContain('Add note');
    });

    it('is not shown to a viewer, who still reads the notes', async () => {
      const { fixture } = await open(viewerMe);
      expect(has(fixture, '#text')).toBe(false);
      expect(has(fixture, 'form')).toBe(false);
      expect(pageText(fixture)).toContain('second note');
      expect(textOf(fixture, '[data-testid="me-role"]')).toBe('viewer');
    });

    it('adds a note: the text is sent, the new note is on top and the field is empty again', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'a brand new note');
      submitForm(fixture);
      const request = ctrl.expectOne('/api/notes');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ text: 'a brand new note' });
      request.flush({ id: 'n3', text: 'a brand new note', author_sub: 'u1', created_at: day(3) }, status(201));
      await settle(fixture);
      const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);
      expect(items).toEqual(['a brand new note', 'second note', 'first note']);
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('');
    });

    it.each([
      ['an empty body', null],
      ['a body that is not a note', { ok: true }],
      ['a note without an id', { text: 'x', created_at: day(3) }],
    ])('a 201 with %s: the list is loaded again instead of inserting it', async (_name, body) => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'saved but unreadable');
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush(body, status(201));
      await settle(fixture);
      const saved = { id: 'n3', text: 'saved but unreadable', author_sub: 'u1', created_at: day(3) };
      ctrl.expectOne('/api/notes').flush([saved, newer, older]);
      await settle(fixture);
      const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);
      expect(items).toEqual(['saved but unreadable', 'second note', 'first note']);
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('');
      expect(pageText(fixture)).not.toContain('The note could not be saved.');
    });

    it('does not send a note of only spaces or an empty one', async () => {
      const { fixture, ctrl } = await open();
      submitForm(fixture);
      typeInto(fixture, '#text', '   \n  ');
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
    });

    it('does not send a note of more than 1000 characters, and says it was not saved', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'x'.repeat(1001));
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
      await settle(fixture);
      expect(pageText(fixture)).toContain('The note could not be saved.');
    });

    it('stops the field at 1000 characters, the same number the check uses', async () => {
      const { fixture } = await open();
      expect((fixture.nativeElement as HTMLElement).querySelector('#text')?.getAttribute('maxlength')).toBe('1000');
    });

    it('sends a note of exactly 1000 characters', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'x'.repeat(1000));
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ id: 'n3', text: 'x'.repeat(1000), author_sub: 'u1', created_at: day(3) }, status(201));
    });

    it('sends one request for a double submit', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'once');
      submitForm(fixture);
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ id: 'n3', text: 'once', author_sub: 'u1', created_at: day(3) }, status(201));
    });

    it('a note that cannot be saved says so, and keeps what was typed', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'precious words');
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ error: 'invalid_request' }, status(400));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The note could not be saved.');
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('precious words');
    });
  });

  describe('a note that was just added and a list that was asked for before', () => {
    // The page opens: the list is asked for, and the person adds a note before that answer arrives.
    async function addBeforeTheList() {
      TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
      const ctrl = TestBed.inject(HttpTestingController);
      await signedInAs(TestBed.inject(AuthService), ctrl);
      const fixture = TestBed.createComponent(NotesPage);
      fixture.detectChanges();
      const list = ctrl.expectOne('/api/notes');
      typeInto(fixture, '#text', 'quick note');
      submitForm(fixture);
      ctrl
        .expectOne((request) => request.method === 'POST')
        .flush({ id: 'n3', text: 'quick note', author_sub: 'u1', created_at: day(3) }, status(201));
      await settle(fixture);
      return { fixture, list };
    }
    const shown = (fixture: { nativeElement: unknown }) =>
      Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);

    it('the late list does not take the added note away', async () => {
      const { fixture, list } = await addBeforeTheList();
      list.flush([newer, older]);
      await settle(fixture);
      expect(shown(fixture)).toEqual(['quick note', 'second note', 'first note']);
    });

    it('the late list that already holds the note shows it once', async () => {
      const { fixture, list } = await addBeforeTheList();
      list.flush([{ id: 'n3', text: 'quick note', author_sub: 'u1', created_at: day(3) }, newer, older]);
      await settle(fixture);
      expect(shown(fixture)).toEqual(['quick note', 'second note', 'first note']);
    });

    it('a late list that fails keeps the added note and says the rest could not be loaded', async () => {
      const { fixture, list } = await addBeforeTheList();
      list.flush('oops', status(500));
      await settle(fixture);
      expect(shown(fixture)).toEqual(['quick note']);
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
    });
  });

  it('a list that cannot be loaded again after an unreadable 201 shows no old list next to the message', async () => {
    const { fixture, ctrl } = await open();
    typeInto(fixture, '#text', 'saved but unreadable');
    submitForm(fixture);
    ctrl.expectOne('/api/notes').flush(null, status(201));
    await settle(fixture);
    ctrl.expectOne('/api/notes').flush('oops', status(500));
    await settle(fixture);
    expect(pageText(fixture)).toContain('The notes could not be loaded.');
    expect(has(fixture, '.note')).toBe(false);
    expect(pageText(fixture)).not.toContain('No notes yet.');
  });

  describe('the notes service says 503 database_unavailable (try again, spec 0006)', () => {
    // With the interceptor of the app in front of the page: a 503 that is not auth_unavailable must not sign the person out.
    async function openWithInterceptor() {
      TestBed.configureTestingModule({
        providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
      });
      const ctrl = TestBed.inject(HttpTestingController);
      const auth = TestBed.inject(AuthService);
      await signedInAs(auth, ctrl);
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
      const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      const fixture = TestBed.createComponent(NotesPage);
      fixture.detectChanges();
      return { fixture, ctrl, auth, navigate, navigateByUrl };
    }

    it('on the list: "The notes could not be loaded.", the header stays, the person stays signed in, no refresh', async () => {
      const { fixture, ctrl, auth, navigate, navigateByUrl } = await openWithInterceptor();
      ctrl.expectOne('/api/notes').flush({ error: 'database_unavailable' }, status(503));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
      expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
      expect(auth.token()).toBe('tok');
      expect(auth.notice()).toBeNull();
      ctrl.expectNone('/auth/refresh');
      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('on adding a note: "The note could not be saved.", what was typed is kept, the person stays signed in, no refresh', async () => {
      const { fixture, ctrl, auth, navigate, navigateByUrl } = await openWithInterceptor();
      ctrl.expectOne('/api/notes').flush([newer, older]);
      await settle(fixture);
      typeInto(fixture, '#text', 'try me again');
      submitForm(fixture);
      ctrl.expectOne((request) => request.method === 'POST').flush({ error: 'database_unavailable' }, status(503));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The note could not be saved.');
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('try me again');
      expect(pageText(fixture)).toContain('second note');
      expect(auth.token()).toBe('tok');
      ctrl.expectNone('/auth/refresh');
      expect(navigate).not.toHaveBeenCalled();
      expect(navigateByUrl).not.toHaveBeenCalled();
      // The same text can be sent again once the database is back.
      submitForm(fixture);
      ctrl
        .expectOne((request) => request.method === 'POST')
        .flush({ id: 'n3', text: 'try me again', author_sub: 'u1', created_at: day(3) }, status(201));
      await settle(fixture);
      expect(pageText(fixture)).not.toContain('The note could not be saved.');
      expect(pageText(fixture)).toContain('try me again');
    });
  });

  it('shows a note as text, never as HTML', async () => {
    const hostile = { id: 'n9', text: '<img src=x onerror="alert(1)"><b>bold</b>', author_sub: 'u', created_at: day(3) };
    const { fixture } = await open(adminMe, [hostile]);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.note img')).toBeNull();
    expect(root.querySelector('.note b')).toBeNull();
    expect(root.querySelector('.note p')?.textContent).toBe('<img src=x onerror="alert(1)"><b>bold</b>');
  });

  describe('a list that cannot be loaded keeps the header and says so', () => {
    it.each([
      ['a 503 auth_unavailable', { error: 'auth_unavailable' }, 503],
      ['a 403 forbidden', { error: 'forbidden' }, 403],
      ['a 502 page of HTML from a proxy', '<html>bad gateway</html>', 502],
      ['a 500', null, 500],
    ])('%s', async (_name, body, code) => {
      TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
      const ctrl = TestBed.inject(HttpTestingController);
      await signedInAs(TestBed.inject(AuthService), ctrl);
      const fixture = TestBed.createComponent(NotesPage);
      fixture.detectChanges();
      ctrl.expectOne('/api/notes').flush(body, status(code));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
      expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
      expect(pageText(fixture)).not.toContain('No notes yet.');
    });

    it('a 200 that is not a list is the same failure', async () => {
      const { fixture } = await open(adminMe, { not: 'a list' });
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
    });

    it.each([
      ['null', [null]],
      ['a number', [1]],
      ['an object without an id', [{ text: 'no id', created_at: day(1) }]],
      ['a good note and a bad one', [newer, { id: 'x' }]],
    ])('a list with %s in it is the same failure, and shows nothing of it', async (_name, list) => {
      const { fixture } = await open(adminMe, list);
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
      expect(has(fixture, '.note')).toBe(false);
    });
  });

  it('asks /auth/me when the person is not known yet', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const ctrl = TestBed.inject(HttpTestingController);
    await holdToken(TestBed.inject(AuthService), ctrl, 'tok');
    const fixture = TestBed.createComponent(NotesPage);
    fixture.detectChanges();
    ctrl.expectOne('/api/notes').flush([]);
    ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(fixture);
    expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
  });

  describe('Sign out', () => {
    it.each([
      ['204', null, 204],
      ['a 500', 'oops', 500],
    ])('calls POST /auth/logout, drops the token and goes to /login after %s', async (_name, body, code) => {
      const { fixture, ctrl, auth, navigateByUrl } = await open();
      clickOn(fixture, '[data-testid="sign-out"]');
      const request = ctrl.expectOne('/auth/logout');
      expect(request.request.method).toBe('POST');
      request.flush(body, status(code));
      await settle(fixture);
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });

    it('a second click while the first sign-out is on its way sends nothing, and the button is off', async () => {
      const { fixture, ctrl, navigateByUrl } = await open();
      clickOn(fixture, '[data-testid="sign-out"]');
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="sign-out"]')?.disabled).toBe(true);
      clickOn(fixture, '[data-testid="sign-out"]');
      ctrl.expectOne('/auth/logout').flush(null, status(204));
      await settle(fixture);
      ctrl.expectNone('/auth/logout');
      expect(navigateByUrl).toHaveBeenCalledTimes(1);
    });

    it('goes to /login when the network fails', async () => {
      const { fixture, ctrl, auth, navigateByUrl } = await open();
      clickOn(fixture, '[data-testid="sign-out"]');
      ctrl.expectOne('/auth/logout').error(new ProgressEvent('error'));
      await settle(fixture);
      expect(auth.token()).toBeNull();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });
  });
});
