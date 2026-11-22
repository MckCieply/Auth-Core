import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { OrgEntry } from '../account-api';
import { AuthService } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, privateMe, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
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

async function open(list: OrgEntry[], notes: object[] = [aNote], creation: 'off' | 'self-service' = 'off') {
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
  ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'off', providers: [], org_creation: creation });
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

const NEW_ID = '77777777-7777-7777-7777-777777777777';
const gammaMe = { ...adminMe, org_id: NEW_ID, org_name: 'Gamma' };
const gammaEntry: OrgEntry = { id: NEW_ID, name: 'Gamma', kind: 'organization', role: { id: 'r3', name: 'admin' }, current: false };
const FORM = '[data-testid="new-company-form"]';
const NAME = '[data-testid="new-company-name"]';
const CREATE = '[data-testid="new-company-submit"]';

function optionTexts(opened: Opened): (string | undefined)[] {
  return Array.from(picker(opened).options).map((option) => option.textContent?.trim());
}

/** Chooses "New company...", which opens the form. */
async function openForm(opened: Opened): Promise<void> {
  const select = picker(opened);
  select.value = 'new-company';
  select.dispatchEvent(new Event('change'));
  await settle(opened.fixture);
}

/** Chooses "New company...", types a name and submits the form; the request is left for the test to answer. */
async function submitName(opened: Opened, name: string): Promise<void> {
  await openForm(opened);
  typeInto(opened.fixture, NAME, name);
  submitForm(opened.fixture, FORM);
  await settle(opened.fixture);
}

function errorText(opened: Opened): string {
  return opened.root.querySelector('[data-testid="new-company-error"]')?.textContent?.trim() ?? '';
}

describe('NotesPage: "New company..." in the picker (spec 0016)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('the picker is shown with one entry when creation is on and hidden when it is off', async () => {
    const on = await open([entries(ACME)[0]], [aNote], 'self-service');
    expect(has(on.fixture, '[data-testid="org-picker"]')).toBe(true);
    expect(optionTexts(on)).toEqual(['Acme', 'New company...']);
    TestBed.resetTestingModule();
    const off = await open([entries(ACME)[0]]);
    expect(has(off.fixture, '[data-testid="org-picker"]')).toBe(false);
  });

  it('"New company..." is the last option when the account has several spaces, and absent when creation is off', async () => {
    const on = await open(entries(ACME), [aNote], 'self-service');
    expect(optionTexts(on)).toEqual(['Acme', 'Private', 'New company...']);
    TestBed.resetTestingModule();
    const off = await open(entries(ACME));
    expect(optionTexts(off)).toEqual(['Acme', 'Private']);
  });

  it('choosing "New company..." opens the form, does not switch and leaves the select on the current company', async () => {
    const opened = await open(entries(ACME), [aNote], 'self-service');
    const switchTo = vi.spyOn(opened.auth, 'switchTo').mockResolvedValue('switched');
    await openForm(opened);
    expect(has(opened.fixture, FORM)).toBe(true);
    expect(switchTo).not.toHaveBeenCalled();
    expect(picker(opened).value).toBe(ACME);
  });

  it('a created company becomes the current one with no notes', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    const create = opened.ctrl.expectOne('/auth/orgs');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ name: 'Gamma' });
    create.flush(gammaEntry, status(201));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/switch').flush({ access_token: 'tok-2' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(gammaMe);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [{ ...entries(ACME)[0], current: false }, { ...gammaEntry, current: true }] });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/notes').flush([]);
    await settle(opened.fixture);
    expect(has(opened.fixture, FORM)).toBe(false);
    expect(opened.root.querySelector('[data-testid="me-company"]')?.textContent).toBe('Gamma');
    expect(picker(opened).value).toBe(NEW_ID);
    expect(pageText(opened.fixture)).not.toContain('Acme minutes');
    expect(opened.root.querySelectorAll('li.note').length).toBe(0);
  });

  it('the new-company form sends one request when submitted twice', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    expect(opened.root.querySelector<HTMLButtonElement>(CREATE)?.disabled).toBe(true);
    clickOn(opened.fixture, CREATE);
    submitForm(opened.fixture, FORM);
    await settle(opened.fixture);
    const sent = opened.ctrl.match('/auth/orgs');
    expect(sent.length).toBe(1);
    sent[0].flush({ error: 'company_limit_reached' }, status(409));
    await settle(opened.fixture);
    expect(opened.root.querySelector<HTMLButtonElement>(CREATE)?.disabled).toBe(false);
  });

  it('the form requires a name: a blank one sends nothing', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, '   ');
    opened.ctrl.expectNone('/auth/orgs');
    expect(has(opened.fixture, FORM)).toBe(true);
    expect(errorText(opened)).toBe('Check the company name.');
  });

  it('409 shows the limit text and keeps the current company', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    opened.ctrl.expectOne('/auth/orgs').flush({ error: 'company_limit_reached' }, status(409));
    await settle(opened.fixture);
    expect(errorText(opened)).toBe('You have reached the limit of companies.');
    expect(opened.root.querySelector('[data-testid="me-company"]')?.textContent).toBe('Acme');
    expect(picker(opened).value).toBe(ACME);
    opened.ctrl.expectNone('/auth/switch');
  });

  it('org_creation_disabled shows its text and hides the entry', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    opened.ctrl.expectOne('/auth/orgs').flush({ error: 'org_creation_disabled' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Creating companies is turned off.');
    expect(has(opened.fixture, FORM)).toBe(false);
    expect(has(opened.fixture, '[data-testid="org-picker"]')).toBe(false);
  });

  it('400 shows the name rule and keeps the form with what was typed', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma ');
    opened.ctrl.expectOne('/auth/orgs').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(errorText(opened)).toBe('Check the company name.');
    expect(opened.root.querySelector<HTMLInputElement>(NAME)?.value).toBe('Gamma ');
  });

  it('a failure shows that something went wrong and keeps the form', async () => {
    const opened = await open([entries(ACME)[0]], [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    opened.ctrl.expectOne('/auth/orgs').flush(null, status(500));
    await settle(opened.fixture);
    expect(errorText(opened)).toBe('Something went wrong. Try again.');
    expect(has(opened.fixture, FORM)).toBe(true);
  });

  it('permissions_changed refreshes the list and says it is up to date', async () => {
    const opened = await open(entries(ACME), [aNote], 'self-service');
    await submitName(opened, 'Gamma');
    opened.ctrl.expectOne('/auth/orgs').flush({ error: 'permissions_changed' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/orgs').flush({ orgs: [entries(PRIVATE)[1]] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('That company is no longer yours. The list is up to date.');
    expect(has(opened.fixture, FORM)).toBe(false);
    expect(optionTexts(opened)).toEqual(['Private', 'New company...']);
  });

  it('the form can be cancelled and leaves the picker as it was', async () => {
    const opened = await open(entries(ACME), [aNote], 'self-service');
    await openForm(opened);
    clickOn(opened.fixture, '[data-testid="new-company-cancel"]');
    await settle(opened.fixture);
    expect(has(opened.fixture, FORM)).toBe(false);
    expect(optionTexts(opened)).toEqual(['Acme', 'Private', 'New company...']);
    expect(picker(opened).value).toBe(ACME);
    opened.ctrl.expectNone('/auth/orgs');
  });
});
