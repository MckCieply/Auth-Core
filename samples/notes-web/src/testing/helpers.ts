import { ComponentFixture } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { AuthService, Me } from '../app/auth/auth.service';

/** Lets pending promises and timers run, then draws the component again. */
export async function settle(fixture?: ComponentFixture<unknown>): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve));
  fixture?.detectChanges();
}

export const adminMe: Me = {
  sub: '11111111-1111-1111-1111-111111111111',
  email: 'admin@example.test',
  org_id: '22222222-2222-2222-2222-222222222222',
  org_name: 'Acme',
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};

export const viewerMe: Me = {
  ...adminMe,
  email: 'viewer@example.test',
  roles: ['viewer'],
  permissions: ['notes:read'],
};

/** Gives the service a token the way the app does: by a refresh that the test answers. */
export async function holdToken(auth: AuthService, ctrl: HttpTestingController, token: string): Promise<void> {
  const done = auth.refresh();
  ctrl.expectOne('/auth/refresh').flush({ access_token: token });
  await done;
}

/** A signed-in person: a token and the answer of /auth/me. */
export async function signedInAs(
  auth: AuthService,
  ctrl: HttpTestingController,
  me: Me = adminMe,
  token = 'tok',
): Promise<void> {
  await holdToken(auth, ctrl, token);
  const loaded = auth.loadMe();
  ctrl.expectOne('/auth/me').flush(me);
  await loaded;
}
