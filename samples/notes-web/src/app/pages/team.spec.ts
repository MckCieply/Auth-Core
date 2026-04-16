import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, holdToken, pageText, privateMe, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { TeamPage } from './team';

const status = (code: number) => ({ status: code, statusText: String(code) });
const manager: Me = { ...adminMe, permissions: [...adminMe.permissions, 'members:manage'] };
const worker = '55555555-5555-5555-5555-555555555555';
const workerUrl = `/auth/org/members/${worker}`;
const members = {
  members: [
    { user_id: adminMe.sub, email: adminMe.email, role: { id: 'r1', name: 'admin' }, joined_at: '2026-04-01T10:00:00Z' },
    { user_id: worker, email: 'worker@example.test', role: { id: 'r2', name: 'user' }, joined_at: '2026-04-02T10:00:00Z' },
  ],
};

async function open(me: Me = manager) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  await signedInAs(TestBed.inject(AuthService), ctrl, me);
  const fixture = TestBed.createComponent(TeamPage);
  fixture.detectChanges();
  return { fixture, ctrl };
}

async function openWithMembers() {
  const opened = await open();
  opened.ctrl.expectOne('/auth/org/members').flush(members);
  await settle(opened.fixture);
  return opened;
}

async function askToRemove(opened: Awaited<ReturnType<typeof openWithMembers>>) {
  clickOn(opened.fixture, '[data-testid="remove"]');
  opened.fixture.detectChanges();
  await settle(opened.fixture);
}

function focused(): string | null {
  return (document.activeElement as HTMLElement | null)?.getAttribute('data-testid') ?? null;
}

describe('TeamPage (spec 0010)', () => {
  it('lists the members with their roles, and "Remove" for each other member only', async () => {
    const { fixture } = await openWithMembers();
    expect(pageText(fixture)).toContain('admin@example.test');
    expect(pageText(fixture)).toContain('worker@example.test');
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="remove"]').length).toBe(1);
  });

  it('names the member in the accessible name of "Remove"', async () => {
    const { fixture } = await openWithMembers();
    const button = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="remove"]');
    expect(button?.getAttribute('aria-label')).toBe('Remove worker@example.test');
  });

  it('shows that it is loading until the person and the list are read', async () => {
    const { fixture, ctrl } = await open();
    expect(has(fixture, '[data-testid="team-loading"]')).toBe(true);
    expect(pageText(fixture)).not.toContain("You don't have access to this.");
    ctrl.expectOne('/auth/org/members').flush(members);
    await settle(fixture);
    expect(has(fixture, '[data-testid="team-loading"]')).toBe(false);
  });

  it('"Remove" asks first, with the sentence that the account will be deleted, and puts the focus on "Cancel"', async () => {
    const opened = await openWithMembers();
    await askToRemove(opened);
    expect(pageText(opened.fixture)).toContain("This person's account will be deleted.");
    expect(has(opened.fixture, '[data-testid="remove-confirmation"][role="alert"]')).toBe(true);
    expect(focused()).toBe('remove-cancel');
    opened.ctrl.expectNone(workerUrl);
  });

  it('the confirmation removes the member, who leaves the list', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    const request = ctrl.expectOne(workerUrl);
    expect(request.request.method).toBe('DELETE');
    request.flush(null, status(204));
    await settle(fixture);
    expect(pageText(fixture)).not.toContain('worker@example.test');
    expect(focused()).toBe('team-title');
  });

  it('"Cancel" leaves everything as it was and gives the focus back to "Remove"', async () => {
    const opened = await openWithMembers();
    await askToRemove(opened);
    clickOn(opened.fixture, '[data-testid="remove-cancel"]');
    opened.fixture.detectChanges();
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).not.toContain("This person's account will be deleted.");
    expect(focused()).toBe('remove');
    opened.ctrl.expectNone(workerUrl);
  });

  it.each([
    ['permission_not_held', 'You cannot remove someone who holds more permissions than you.'],
    ['last_manager', 'The company needs at least one person who manages its members.'],
    ['cannot_change_self', 'You cannot remove yourself.'],
  ])('%s: one sentence, and the member stays', async (code, sentence) => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne(workerUrl).flush({ error: code }, status(code === 'permission_not_held' ? 403 : 409));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sentence);
    expect(pageText(fixture)).toContain('worker@example.test');
    expect(focused()).toBe('remove');
  });

  it('not_found (someone else removed the member): the list is read again and no error is shown', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne(workerUrl).flush({ error: 'not_found' }, status(404));
    await settle(fixture);
    ctrl.expectOne('/auth/org/members').flush({ members: [members.members[0]] });
    await settle(fixture);
    expect(pageText(fixture)).not.toContain('worker@example.test');
    expect(pageText(fixture)).not.toContain('Something went wrong');
  });

  it('any other failure of the removal says that something went wrong, and the member stays', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne(workerUrl).flush({ error: 'database_unavailable' }, status(500));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(fixture)).toContain('worker@example.test');
  });

  it.each(['forbidden', 'permissions_changed'])('a 403 %s that is left after the refresh reloads the person and the list', async (code) => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne(workerUrl).flush({ error: code }, status(403));
    await settle(fixture);
    ctrl.expectOne('/auth/me').flush(viewerMe);
    await settle(fixture);
    expect(pageText(fixture)).toContain("You don't have access to this.");
    expect(has(fixture, '[data-testid="member"]')).toBe(false);
  });

  it('a 403 after which the person still manages members reloads the list and says so', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne(workerUrl).flush({ error: 'permissions_changed' }, status(403));
    await settle(fixture);
    ctrl.expectOne('/auth/me').flush(manager);
    await settle(fixture);
    ctrl.expectOne('/auth/org/members').flush(members);
    await settle(fixture);
    expect(pageText(fixture)).toContain("You don't have access to this.");
    expect(pageText(fixture)).toContain('worker@example.test');
  });

  it('a person without members:manage is told so and nothing is listed', async () => {
    const { fixture, ctrl } = await open(viewerMe);
    await settle(fixture);
    ctrl.expectNone('/auth/org/members');
    expect(pageText(fixture)).toContain("You don't have access to this.");
    expect(has(fixture, '[data-testid="member"]')).toBe(false);
  });

  it('a private account that reaches /team is told so too: no call for members, nothing listed', async () => {
    const { fixture, ctrl } = await open(privateMe);
    await settle(fixture);
    ctrl.expectNone('/auth/org/members');
    expect(pageText(fixture)).toContain("You don't have access to this.");
    expect(has(fixture, '[data-testid="member"]')).toBe(false);
    expect(has(fixture, '[data-testid="remove"]')).toBe(false);
  });

  it('a person who cannot be read is not told they have no access: the page says that something went wrong, and tries again', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const ctrl = TestBed.inject(HttpTestingController);
    await holdToken(TestBed.inject(AuthService), ctrl, 'tok');
    const fixture = TestBed.createComponent(TeamPage);
    fixture.detectChanges();
    ctrl.expectOne('/auth/me').flush({ error: 'database_unavailable' }, status(500));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(fixture)).not.toContain("You don't have access to this.");
    clickOn(fixture, '[data-testid="try-again"]');
    ctrl.expectOne('/auth/me').flush(manager);
    await settle(fixture);
    ctrl.expectOne('/auth/org/members').flush(members);
    await settle(fixture);
    expect(pageText(fixture)).toContain('worker@example.test');
  });
});
