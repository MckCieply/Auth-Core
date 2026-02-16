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

function root(fixture: ComponentFixture<unknown>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function find<T extends Element>(fixture: ComponentFixture<unknown>, selector: string): T {
  const element = root(fixture).querySelector<T>(selector);
  if (element === null) {
    throw new Error(`nothing matches ${selector} in: ${pageText(fixture)}`);
  }
  return element;
}

/** All the text on the page, white space collapsed. */
export function pageText(fixture: ComponentFixture<unknown>): string {
  return (root(fixture).textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** Types into an input or a text area the way a person does: the value, then the event the form listens for. */
export function typeInto(fixture: ComponentFixture<unknown>, selector: string, value: string): void {
  const field = find<HTMLInputElement | HTMLTextAreaElement>(fixture, selector);
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

/**
 * Like typeInto, but the value arrives exactly as given. A browser (and jsdom) strips the white space around the value of an
 * input of type email, so a test of what the page does with spaces around an address must make the field a plain text field
 * first.
 */
export function typeRaw(fixture: ComponentFixture<unknown>, selector: string, value: string): void {
  const field = find<HTMLInputElement | HTMLTextAreaElement>(fixture, selector);
  if (field instanceof HTMLInputElement) {
    field.type = 'text';
  }
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

export function submitForm(fixture: ComponentFixture<unknown>, selector = 'form'): void {
  find<HTMLFormElement>(fixture, selector).dispatchEvent(new Event('submit'));
}

export function clickOn(fixture: ComponentFixture<unknown>, selector: string): void {
  find<HTMLElement>(fixture, selector).click();
}

export function has(fixture: ComponentFixture<unknown>, selector: string): boolean {
  return root(fixture).querySelector(selector) !== null;
}

export function valueOf(fixture: ComponentFixture<unknown>, selector: string): string {
  return find<HTMLInputElement>(fixture, selector).value;
}
