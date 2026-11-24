import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, Injector, OnInit, afterNextRender, computed, inject, input, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AccountApi } from '../account-api';
import { AuthService, LoginResult, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

/**
 * True for a 503 {"error":"auth_unavailable"}: the notes service cannot reach Auth-Core. The interceptor shows the bar "Try again
 * shortly." for that answer (spec 0007, session table), so the section adds no sentence of its own next to it.
 */
function isAuthUnavailable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
}

/**
 * "Delete account" (spec 0010; spec 0011 for an account without a password; spec 0012: on the settings screen). The product's notes go first
 * (DELETE /api/me), then the account (DELETE /auth/account), with the password or a provider ticket. An account that is a member of any
 * company (spec 0016) is not deleted here: the section names the companies and has no button.
 */
@Component({
  selector: 'app-delete-account',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section data-testid="delete-account-section">
      <h2>{{ t.deleteAccount }}</h2>
      @if (moved()) {
        <p class="status" role="status" data-testid="account-moved">{{ t.movedToCompany }}</p>
      }
      @if (memberOf().length > 0) {
        <p data-testid="delete-company-member">{{ o.companyMember(memberOf().join(', ')) }}</p>
      } @else if (me() !== null) {
        @if (!deleting()) {
          <button type="button" data-testid="delete-account" (click)="askToDelete()">{{ t.deleteAccount }}</button>
        } @else {
          <div data-testid="delete-section">
            <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
            @if (me()?.has_password !== false) {
              <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
                <label for="delete-password">{{ password }}</label>
                <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
                <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
                <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ cancel }}</button>
              </form>
            } @else if (reauthTicket() === null) {
              <!-- Spec 0011: no password to ask for; the provider confirms. Nothing is deleted before the person comes back. -->
              <app-provider-buttons
                [providers]="me()?.linked_providers ?? []"
                action="confirm"
                [reauth]="true"
                target="delete_account"
                [separator]="false"
              />
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ cancel }}</button>
            } @else {
              <button type="button" data-testid="delete-confirm" [disabled]="deleteBusy()" (click)="deleteWithTicket()">{{ t.deleteConfirm }}</button>
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ cancel }}</button>
            }
            @if (deleteError(); as text) {
              <p class="error" role="alert" data-testid="delete-error">{{ text }}</p>
            }
          </div>
        }
      }
    </section>
  `,
})
export class DeleteAccountSection implements OnInit {
  protected readonly t = texts.notes;
  protected readonly o = texts.orgs;
  protected readonly somethingWrong = texts.common.somethingWrong;
  protected readonly password = texts.common.password;
  protected readonly cancel = texts.common.cancel;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly me = this.auth.me;
  /** The names of the companies the account is a member of (GET /auth/orgs, read when the section starts). */
  protected readonly companyNames = signal<string[]>([]);
  /** Every company the account is known to belong to: the listed ones, and the space of the session when it is a company's. */
  protected readonly memberOf = computed(() => {
    const names = this.companyNames();
    const person = this.me();
    return person !== null && person.org_kind === 'organization' && !names.includes(person.org_name) ? [...names, person.org_name] : names;
  });
  protected readonly deleteForm = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly deleting = signal(false);
  protected readonly deleteBusy = signal(false);
  protected readonly deleteError = signal<string | null>(null);
  // The account turned out to belong to a company while it was being deleted: the section says so, once.
  protected readonly moved = signal(false);
  /**
   * The ticket of "Confirm with {Provider}" that /provider-return handed on, read by the routed page (`SettingsPage`) while the navigation that
   * brought it is current, and passed down: this section is created later, when `currentNavigation()` is already null. Memory only; the page
   * also takes it out of the history entry.
   */
  readonly handedTicket = input<string | null>(null, { alias: 'ticket' });
  protected readonly reauthTicket = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const handed = this.handedTicket();
    if (handed !== null) {
      this.reauthTicket.set(handed);
      this.deleting.set(true);
      this.focusDeleteSection();
    }
    // A list that cannot be read shows the button: the check before a deletion (`stillInACompany`) catches it at the click.
    const names = await this.companyNamesOnServer();
    if (names !== null) {
      this.companyNames.set(names);
    }
  }

  protected askToDelete(): void {
    if (this.deleteBusy()) {
      return;
    }
    this.deleteForm.reset();
    this.deleteError.set(null);
    this.moved.set(false);
    this.deleting.set(true);
    this.focusDeleteSection();
  }

  /** The person keeps the account (the notes that were deleted stay deleted). */
  protected cancelDelete(): void {
    this.deleting.set(false);
    this.reauthTicket.set(null);
  }

  /** Spec 0011: "Delete my account" after a provider confirmation. The notes first, then the account, as with a password. */
  protected async deleteWithTicket(): Promise<void> {
    const ticket = this.reauthTicket();
    if (this.deleteBusy() || ticket === null) {
      return;
    }
    if (await this.stillInACompany()) {
      return;
    }
    if (!(await this.deleteNotes())) {
      return;
    }
    const result = await this.auth.deleteAccountWithTicket(ticket);
    if (result.ok) {
      await this.leave();
      return;
    }
    if (result.failure.kind === 'reauth_failed') {
      // The ticket is used up or was never good: the person confirms again.
      this.reauthTicket.set(null);
      this.deleteError.set(this.t.reauthFailed);
      this.deleteBusy.set(false);
      return;
    }
    await this.refused(result);
  }

  /**
   * Spec 0010, Decision 8: the product deletes its own data first (DELETE /api/me), and only after its 204 the account (DELETE /auth/account).
   * Auth-Core does not tell the product, so the other order could leave notes that nobody can reach.
   */
  protected async deleteAccount(): Promise<void> {
    const password = this.deleteForm.controls.password.value;
    if (this.deleteBusy()) {
      return;
    }
    if (!acceptablePassword(password)) {
      this.deleteError.set(texts.common.passwordRequired);
      return;
    }
    if (await this.stillInACompany()) {
      return;
    }
    if (!(await this.deleteNotes())) {
      return;
    }
    const result = await this.auth.deleteAccount(password);
    if (result.ok) {
      await this.leave();
      return;
    }
    await this.refused(result);
  }

  /**
   * Spec 0016: an account that is still a member of a company cannot be deleted, so nothing is deleted before the sample knows it is not
   * (the server's `403 company_member` stays the guard, see `refused`). True when the deletion must stop: a company is listed, and the
   * section says which; or the list could not be read, and the section says something went wrong. Nothing was deleted either way.
   */
  private async stillInACompany(): Promise<boolean> {
    this.deleteBusy.set(true);
    this.deleteError.set(null);
    const names = await this.companyNamesOnServer();
    this.deleteBusy.set(false);
    if (names === null) {
      this.deleteError.set(this.somethingWrong);
      return true;
    }
    if (names.length === 0) {
      return false;
    }
    this.deleteError.set(this.o.companyMember(names.join(', ')));
    return true;
  }

  /** The names of the companies in a fresh GET /auth/orgs, or null when the list cannot be read. */
  private async companyNamesOnServer(): Promise<string[] | null> {
    const listed = await this.account.listOrgs();
    return listed.ok ? listed.value.filter((entry) => entry.kind === 'organization').map((entry) => entry.name) : null;
  }

  /**
   * DELETE /api/me, the first half of every deletion (spec 0010, Decision 8). True when the notes are gone; false when nothing was deleted
   * (the person may try again, and the section says so).
   */
  private async deleteNotes(): Promise<boolean> {
    this.deleteBusy.set(true);
    this.deleteError.set(null);
    try {
      await firstValueFrom(this.http.delete('/api/me'));
    } catch (error) {
      // auth_unavailable: the bar says it. Nothing was deleted: the person may try again.
      this.deleteError.set(isAuthUnavailable(error) ? null : this.somethingWrong);
      this.deleteBusy.set(false);
      return false;
    }
    return true;
  }

  private async leave(): Promise<void> {
    try {
      await this.router.navigateByUrl('/login', { state: { notice: 'deleted' } });
    } catch {
      // nothing to show: the account is gone and so is the session
    }
  }

  /** The refusals both deletions share, after the notes are gone. */
  private async refused(result: LoginResult & { ok: false }): Promise<void> {
    if (result.failure.kind === 'company_member' && (await this.refusedAsCompanyMember())) {
      // The text with the names is shown; the account stays.
    } else if (result.failure.kind === 'password_required' || result.failure.kind === 'reauth_required') {
      // Spec 0011: the account is not what this screen thought (a password was set, or it has none): draw the section that fits it now.
      this.reauthTicket.set(null);
      this.deleteForm.reset();
      await this.auth.loadMe();
      this.deleteError.set(this.t.notesGone);
    } else if (result.failure.kind === 'wrong_password') {
      this.deleteError.set(this.t.wrongPassword);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.deleteError.set(`${texts.login.tooManyAttempts(result.failure.retryAfterSeconds)} ${this.t.notesGone}`);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
      this.deleteError.set(this.t.notesGone);
    } else if (await this.leftThePrivateSpace()) {
      // Nothing to delete here any more: the section says the account belongs to a company now.
    } else {
      this.deleteError.set(`${this.somethingWrong} ${this.t.notesGone}`);
    }
    this.deleteBusy.set(false);
  }

  /** `403 company_member` after the notes are gone (a company was created since the check): the text with the names from a fresh list. */
  private async refusedAsCompanyMember(): Promise<boolean> {
    const names = await this.companyNamesOnServer();
    if (names === null || names.length === 0) {
      return false;
    }
    this.deleteError.set(`${this.o.companyMember(names.join(', '))} ${this.t.notesGone}`);
    this.deleteBusy.set(false);
    return true;
  }

  /**
   * True when the person turns out to belong to a company (an invitation was accepted meanwhile, so Auth-Core answered permissions_changed
   * and then forbidden). The delete state is reset and one sentence says what happened.
   */
  private async leftThePrivateSpace(): Promise<boolean> {
    await this.auth.loadMe();
    const person = this.me();
    if (person === null || person.org_kind === 'personal') {
      return false;
    }
    this.deleting.set(false);
    this.deleteError.set(null);
    this.moved.set(true);
    return true;
  }

  /**
   * The keyboard and a screen reader follow the section that opens: to the password field, else the first "Confirm with ..." link
   * (spec 0011), else "Delete my account" (back from the provider with the ticket).
   */
  private focusDeleteSection(): void {
    afterNextRender(
      () => {
        const root = this.host.nativeElement;
        const target =
          root.querySelector<HTMLElement>('#delete-password') ??
          root.querySelector<HTMLElement>('[data-testid="delete-section"] a') ??
          root.querySelector<HTMLElement>('[data-testid="delete-confirm"]');
        target?.focus();
      },
      { injector: this.injector },
    );
  }
}
