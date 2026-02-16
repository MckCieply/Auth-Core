import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, pageText, settle, submitForm, typeInto, valueOf } from '../../testing/helpers';
import { InvitePage } from './invite';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This invitation has expired or was already used. Ask for a new one.';
const preview = { org_name: 'Acme', email: 'new@acme.example', role: 'viewer' };

async function open(query = '?token=tok-i', previewAnswer: 'ok' | 'wait' = 'ok') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'invite', component: InvitePage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/invite${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/invite${query}`, InvitePage);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  if (previewAnswer === 'ok') {
    ctrl.expectOne('/auth/invites/preview').flush(preview);
    await settle(harness.fixture);
  }
  return { fixture: harness.fixture, ctrl, navigate };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

describe('InvitePage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for the preview with the token on opening, and takes the token out of the address bar', async () => {
    const { ctrl } = await open('?token=tok-i', 'wait');
    const request = ctrl.expectOne('/auth/invites/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-i' });
    expect(window.location.search).toBe('');
    request.flush(preview);
  });

  it('shows "Join {company} as {role}", the email (not editable) and who the password is for', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain('Join Acme as viewer');
    expect(valueOf(fixture, '#email')).toBe('new@acme.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(pageText(fixture)).toContain('This sets the password for new@acme.example.');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
  });

  it('204: goes to /login with the email in the navigation state, not in the URL', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-i', password: 'Joined-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledTimes(1);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'new@acme.example' } });
  });

  it('sends the password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  Joined Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.body).toEqual({ token: 'tok-i', password: '  Joined Passw0rd  ' });
    request.flush(null, status(204));
  });

  it('weak_password: the rules not met, one line each', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'weak_password', rules: ['too_short', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('409 already_member on accept: "This account already belongs to a company."', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'already_member' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('This account already belongs to a company.');
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it('409 already_member on the preview: the same sentence, and no form', async () => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush({ error: 'already_member' }, status(409));
    await settle(fixture);
    expect(pageText(fixture)).toContain('This account already belongs to a company.');
    expect(has(fixture, 'form')).toBe(false);
  });

  it('invalid_token on the preview: "This invitation has expired or was already used. Ask for a new one." and no link to /forgot', async () => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush({ error: 'invalid_token' }, status(400));
    await settle(fixture);
    expect(pageText(fixture)).toContain(invalid);
    expect(pageText(fixture)).not.toContain('This link has expired');
    expect(has(fixture, 'a')).toBe(false);
    expect(has(fixture, 'form')).toBe(false);
  });

  it('invalid_token on accept: the same sentence, and no link', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a')).toBe(false);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s on the preview: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it('500 on accept: "Something went wrong. Try again.", and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd', 'Joined-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/invites/accept');
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid invitation, with no request', async (query) => {
    const { fixture, ctrl } = await open(query, 'wait');
    expect(pageText(fixture)).toContain(invalid);
    expect(has(fixture, 'a')).toBe(false);
    ctrl.expectNone('/auth/invites/preview');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/accept').flush(null, status(204));
  });
});
