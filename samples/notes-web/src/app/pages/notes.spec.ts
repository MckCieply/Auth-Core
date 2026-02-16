import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
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

    it('does not send a note of only spaces or an empty one', async () => {
      const { fixture, ctrl } = await open();
      submitForm(fixture);
      typeInto(fixture, '#text', '   \n  ');
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
    });

    it('does not send a note of more than 1000 characters', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'x'.repeat(1001));
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
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
