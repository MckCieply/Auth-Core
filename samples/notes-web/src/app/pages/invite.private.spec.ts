import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { clickOn, has, pageText, realLocation, settle, submitForm, typeInto } from '../../testing/helpers';
import { InvitePage } from './invite';

const status = (code: number) => ({ status: code, statusText: String(code) });
// Spec 0017: an address with a private account is an existing account like any other; the server says has_account too.
const preview = { org_name: 'Acme', email: 'ola@poczta.example', role: 'user', has_private_account: true, has_account: true, org_id: 'org-acme' };
const line = 'You already have an account with ola@poczta.example. Accepting adds Acme to it; everything else stays as it is.';

async function open(answer: object = preview) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'invite', component: InvitePage }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
  });
  history.replaceState(null, '', '/invite?token=tok-i');
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/invite?token=tok-i', InvitePage);
  await settle();
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/invites/preview').flush(answer);
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl, navigate };
}

describe('InvitePage for an address with a private account (specs 0010 and 0017)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('shows the line of an existing account and one button, and no warning, no move button and no password field', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain(line);
    expect(has(fixture, '[data-testid="invite-existing"]')).toBe(true);
    expect(has(fixture, '[data-testid="accept"]')).toBe(true);
    expect(has(fixture, '[data-testid="invite-private-warning"]')).toBe(false);
    expect(has(fixture, '[data-testid="move"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('private account');
    expect(pageText(fixture)).not.toContain('deleted');
    expect(has(fixture, '#password')).toBe(false);
    expect(has(fixture, '#repeat')).toBe(false);
  });

  it('sends nothing until the button is pressed', async () => {
    const { ctrl } = await open();
    ctrl.expectNone('/auth/invites/accept');
  });

  it('the button sends the token alone, never leave_private_account, then opens the sign-in with the email', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="accept"]');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.body).toEqual({ token: 'tok-i' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'ola@poczta.example' } });
  });

  it('an invitation that was used meanwhile says so', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="accept"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('This invitation has expired or was already used. Ask for a new one.');
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('a limit (429) leaves the button to press again, and a failure says that something went wrong', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="accept"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'too_many_requests', retry_after_seconds: 30 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).not.toContain('Something went wrong');
    expect((opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="accept"]')?.disabled).toBe(false);
    clickOn(opened.fixture, '[data-testid="accept"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'database_unavailable' }, status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('without an account the screen is unchanged', async () => {
    const { fixture } = await open({ ...preview, has_private_account: false, has_account: false });
    expect(pageText(fixture)).not.toContain('private account');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '[data-testid="accept"]')).toBe(false);
  });

  it('409 private_account_exists from an older server: the generic error and no second preview', async () => {
    const opened = await open({ ...preview, has_private_account: false, has_account: false });
    typeInto(opened.fixture, '#password', 'Joined-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'Joined-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'private_account_exists' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/invites/preview');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain('private account');
  });
});
