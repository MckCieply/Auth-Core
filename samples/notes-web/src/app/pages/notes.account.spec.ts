import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, has, pageText, privateMe, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { NotesPage } from './notes';

async function open(me: Me, notes: object[] = []) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush(notes);
  ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'off', providers: [], org_creation: 'off' });
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

describe('NotesPage: the account bar (spec 0010)', () => {
  it('a private account sees its address, "Private account" and "Settings", and no "Delete account", company, role or Team (spec 0012)', async () => {
    const { fixture } = await open(privateMe);
    expect(pageText(fixture)).toContain('ola@poczta.example');
    expect(pageText(fixture)).toContain('Private account');
    expect(has(fixture, '[data-testid="settings-link"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(false);
    expect(has(fixture, '[data-testid="me-role"]')).toBe(false);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });

  it('the_notes_screen_has_no_delete_account_button_for_a_private_account', async () => {
    const { fixture } = await open(privateMe);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
    expect(has(fixture, '[data-testid="delete-section"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('Delete account');
  });

  it('a company member who manages members sees "Team", and no "Delete account"', async () => {
    const { fixture } = await open({ ...adminMe, permissions: [...adminMe.permissions, 'members:manage'] });
    expect(has(fixture, 'a[href="/team"][data-testid="team-link"]')).toBe(true);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('a company member without members:manage sees no "Team"', async () => {
    const { fixture } = await open(viewerMe);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });

  it('a holder of org:delete sees "Team" without members:manage (spec 0012)', async () => {
    const { fixture } = await open({ ...adminMe, permissions: [...adminMe.permissions, 'org:delete'] });
    expect(has(fixture, 'a[href="/team"][data-testid="team-link"]')).toBe(true);
  });
});
