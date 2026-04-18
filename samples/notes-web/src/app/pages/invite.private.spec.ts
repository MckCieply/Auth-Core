import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { clickOn, has, pageText, realLocation, settle, submitForm, typeInto } from '../../testing/helpers';
import { InvitePage } from './invite';

const status = (code: number) => ({ status: code, statusText: String(code) });
const preview = { org_name: 'Acme', email: 'ola@poczta.example', role: 'user', has_private_account: true };
const warning =
  'You already have a private account with this email address. If you accept, you move to Acme: the space of your private account is ' +
  'deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of Acme.';

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

describe('InvitePage for an address with a private account (spec 0010)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('shows the warning in red and one button, and no password field', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain(warning);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="invite-private-warning"]')?.className).toContain('error');
    expect(pageText(fixture)).toContain('Move me to the company and delete my private account');
    expect(has(fixture, '#password')).toBe(false);
    expect(has(fixture, '#repeat')).toBe(false);
  });

  it('sends nothing until the button is pressed', async () => {
    const { ctrl } = await open();
    ctrl.expectNone('/auth/invites/accept');
  });

  it('the button sends leave_private_account true and no password, then opens the sign-in with the email', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="move"]');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.body).toEqual({ token: 'tok-i', leave_private_account: true });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'ola@poczta.example' } });
  });

  it('an invitation that was used meanwhile says so', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="move"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('This invitation has expired or was already used. Ask for a new one.');
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('a limit (429) leaves the button to press again, and a failure says that something went wrong', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="move"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'too_many_requests', retry_after_seconds: 30 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).not.toContain('Something went wrong');
    expect((opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="move"]')?.disabled).toBe(false);
    clickOn(opened.fixture, '[data-testid="move"]');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'database_unavailable' }, status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('without a private account the screen is unchanged', async () => {
    const { fixture } = await open({ ...preview, has_private_account: false });
    expect(pageText(fixture)).not.toContain('private account');
    expect(has(fixture, '#password')).toBe(true);
  });

  it('409 private_account_exists on an acceptance asks the preview again and shows the warning', async () => {
    const opened = await open({ ...preview, has_private_account: false });
    typeInto(opened.fixture, '#password', 'Joined-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'Joined-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'private_account_exists' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/preview').flush(preview);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(warning);
  });
});
