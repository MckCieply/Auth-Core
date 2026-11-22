import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { OrgEntry } from '../account-api';
import { AuthService } from '../auth/auth.service';
import { adminMe, has, pageText, privateMe, settle, signedInAs } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ACME = adminMe.org_id;
const PRIVATE = privateMe.org_id;
const aNote = { id: 'n1', text: 'Acme minutes', author_sub: adminMe.sub, created_at: '2026-04-10T10:00:00Z' };
const otherNote = { id: 'n2', text: 'Buy oat milk', author_sub: adminMe.sub, created_at: '2026-04-11T10:00:00Z' };

function entries(current: string): OrgEntry[] {
  return [
    { id: ACME, name: 'Acme', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: current === ACME },
    { id: PRIVATE, name: 'private', kind: 'personal', role: { id: 'r2', name: 'private' }, current: current === PRIVATE },
  ];
}

async function open(list: OrgEntry[], notes: object[] = [aNote]) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, adminMe);
  const loading = auth.loadOrgs();
  ctrl.expectOne('/auth/orgs').flush({ orgs: list });
  await loading;
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush(notes);
  ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'off', providers: [], org_creation: 'off' });
  await settle(fixture);
  return { fixture, ctrl, auth, root: fixture.nativeElement as HTMLElement };
}

type Opened = Awaited<ReturnType<typeof open>>;

function picker(opened: Opened): HTMLSelectElement {
  const select = opened.root.querySelector<HTMLSelectElement>('[data-testid="org-picker"]');
  if (select === null) {
    throw new Error(`no picker in: ${pageText(opened.fixture)}`);
  }
  return select;
}

function choose(opened: Opened, id: string): void {
  const select = picker(opened);
  select.value = id;
  select.dispatchEvent(new Event('change'));
}

/** The three answers of a switch that went through: the token, the person, the list. */
async function answerSwitch(opened: Opened, me: typeof adminMe, current: string): Promise<void> {
  opened.ctrl.expectOne('/auth/switch').flush({ access_token: 'tok-2' });
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/me').flush(me);
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/orgs').flush({ orgs: entries(current) });
  await settle(opened.fixture);
}

describe('NotesPage: the company picker (spec 0015)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('one entry: no picker', async () => {
    const opened = await open([entries(ACME)[0]]);
    expect(has(opened.fixture, '[data-testid="org-picker"]')).toBe(false);
  });

  it('no list yet: no picker', async () => {
    const opened = await open([]);
    expect(has(opened.fixture, '[data-testid="org-picker"]')).toBe(false);
  });

  it('two entries: the picker with the company and "Private", labelled "Company", the current one selected', async () => {
    const opened = await open(entries(ACME));
    const select = picker(opened);
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual(['Acme', 'Private']);
    expect(Array.from(select.options).map((option) => option.value)).toEqual([ACME, PRIVATE]);
    expect(select.value).toBe(ACME);
    expect(opened.root.querySelector(`label[for="${select.id}"]`)?.textContent?.trim()).toBe('Company');
  });

  it('the personal space is the selected one when it is the current one', async () => {
    const opened = await open(entries(PRIVATE));
    expect(picker(opened).value).toBe(PRIVATE);
  });

  it('"Team" and "Delete account" still follow /auth/me, not the picker', async () => {
    const opened = await open(entries(ACME));
    expect(has(opened.fixture, '[data-testid="me-company"]')).toBe(true);
    expect(has(opened.fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('choosing the other space calls switchTo with its id', async () => {
    const opened = await open(entries(ACME));
    const switchTo = vi.spyOn(opened.auth, 'switchTo').mockResolvedValue('switched');
    choose(opened, PRIVATE);
    await settle(opened.fixture);
    expect(switchTo).toHaveBeenCalledExactlyOnceWith(PRIVATE);
  });

  it('choosing the space that is already current does nothing', async () => {
    const opened = await open(entries(ACME));
    const switchTo = vi.spyOn(opened.auth, 'switchTo').mockResolvedValue('switched');
    choose(opened, ACME);
    await settle(opened.fixture);
    expect(switchTo).not.toHaveBeenCalled();
  });

  it('a switch that went through reloads the notes of the new space and shows the new account bar', async () => {
    const opened = await open(entries(ACME));
    expect(pageText(opened.fixture)).toContain('Acme minutes');
    choose(opened, PRIVATE);
    await answerSwitch(opened, privateMe, PRIVATE);
    opened.ctrl.expectOne('/api/notes').flush([otherNote]);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
    expect(pageText(opened.fixture)).not.toContain('Acme minutes');
    expect(has(opened.fixture, '[data-testid="me-private"]')).toBe(true);
    expect(picker(opened).value).toBe(PRIVATE);
  });

  it('an answer for the space the person left meanwhile is not shown: the last space wins', async () => {
    const opened = await open(entries(ACME));
    choose(opened, PRIVATE);
    await answerSwitch(opened, privateMe, PRIVATE);
    choose(opened, ACME);
    await answerSwitch(opened, adminMe, ACME);
    const [first, second] = opened.ctrl.match('/api/notes');
    second.flush([aNote]);
    await settle(opened.fixture);
    first.flush([otherNote]);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Acme minutes');
    expect(pageText(opened.fixture)).not.toContain('Buy oat milk');
  });

  it('"gone": says so, and the picker follows the list that was loaded again', async () => {
    const opened = await open(entries(ACME));
    choose(opened, PRIVATE);
    opened.ctrl.expectOne('/auth/switch').flush({ error: 'not_found' }, status(404));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [entries(ACME)[0]] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('That company is no longer yours. The list is up to date.');
    expect(has(opened.fixture, '[data-testid="org-picker"]')).toBe(false);
  });

  it('a switch that failed says something went wrong and keeps the current space selected', async () => {
    const opened = await open(entries(ACME));
    choose(opened, PRIVATE);
    opened.ctrl.expectOne('/auth/switch').flush(null, status(503));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(picker(opened).value).toBe(ACME);
  });

  it('another tab switched: the refresh brings another org_id and the notes are read again', async () => {
    const opened = await open(entries(ACME));
    const refreshing = opened.auth.refresh();
    opened.ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-2' });
    await refreshing;
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: entries(PRIVATE) });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/notes').flush([otherNote]);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Buy oat milk');
    expect(pageText(opened.fixture)).not.toContain('Acme minutes');
    expect(opened.auth.notice()).toBe('switched');
  });

  it('a refresh with the same org_id does not read the notes again', async () => {
    const opened = await open(entries(ACME));
    const refreshing = opened.auth.refresh();
    opened.ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-2' });
    await refreshing;
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(opened.fixture);
    opened.ctrl.expectNone('/api/notes');
    expect(pageText(opened.fixture)).toContain('Acme minutes');
  });
});
