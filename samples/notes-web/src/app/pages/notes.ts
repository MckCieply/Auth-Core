import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, Injector, OnInit, afterNextRender, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, LoginResult, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { ProviderButtons } from './provider-buttons';

interface Note {
  id: string;
  text: string;
  author_sub: string;
  created_at: string;
}

const MAX_NOTE_CHARACTERS = 1000;

// What .NET calls white space, which is what Auth-Core calls a blank password (spec 0010: 400 invalid_request).
const BLANK = /^[\t-\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$/;
// A surrogate half with no partner: JSON carries it as an escape, and Auth-Core refuses a body that holds one.
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
const MAX_BODY_BYTES = 8 * 1024;

/**
 * True for a password that Auth-Core will not refuse as a malformed request: not blank, no NUL, no lone surrogate and a body that fits.
 * The notes are deleted before the account call, so a request that is certain to fail must not get that far.
 */
function acceptablePassword(password: string): boolean {
  return (
    !BLANK.test(password) &&
    !password.includes('\0') &&
    !LONE_SURROGATE.test(password) &&
    new TextEncoder().encode(JSON.stringify({ password })).length <= MAX_BODY_BYTES
  );
}

/**
 * True for a 503 {"error":"auth_unavailable"}: the notes service cannot reach Auth-Core. The interceptor shows the bar "Try again
 * shortly." for that answer (spec 0007, session table), so the screen adds no sentence of its own next to it.
 */
function isAuthUnavailable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
}

/** The ticket of a confirmation that /provider-return handed on (spec 0011), or null. Only in memory: never in a URL or in storage. */
function reauthTicketOf(router: Router): string | null {
  const ticket: unknown = router.currentNavigation()?.extras.state?.['reauthTicket'];
  return typeof ticket === 'string' && ticket !== '' ? ticket : null;
}

/** True for what the service sends as a note: the view shows the text and the time, and tracks the id. */
function isNote(value: unknown): value is Note {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const note = value as Record<string, unknown>;
  // The time is shown by the date pipe, which throws on text that is no date: a note like that would take the whole list down.
  return (
    typeof note['id'] === 'string' &&
    typeof note['text'] === 'string' &&
    typeof note['created_at'] === 'string' &&
    !Number.isNaN(new Date(note['created_at']).getTime())
  );
}

@Component({
  selector: 'app-notes',
  imports: [ReactiveFormsModule, DatePipe, RouterLink, ProviderButtons],
  template: `
    <main class="wide">
      <header class="top">
        @if (me(); as person) {
          <p class="who">
            <strong data-testid="me-email">{{ person.email }}</strong>
            @if (person.org_kind === 'personal') {
              <span data-testid="me-private">{{ t.privateAccount }}</span>
            } @else {
              <span data-testid="me-company">{{ person.org_name }}</span>
              <span data-testid="me-role">{{ person.roles.join(', ') }}</span>
              @if (canManageMembers()) {
                <a routerLink="/team" data-testid="team-link">{{ t.team }}</a>
              }
            }
          </p>
          @if (person.org_kind === 'personal') {
            <button type="button" data-testid="delete-account" (click)="askToDelete()">{{ t.deleteAccount }}</button>
          }
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
      </header>
      <h1>{{ t.title }}</h1>
      @if (moved()) {
        <p class="status" role="status" data-testid="account-moved">{{ t.movedToCompany }}</p>
      }
      @if (canWrite() && !notesGone()) {
        <form [formGroup]="form" (ngSubmit)="add()" novalidate>
          <label for="text">{{ t.newNote }}</label>
          <textarea id="text" rows="3" formControlName="text" [attr.maxlength]="maxCharacters"></textarea>
          <button type="submit" [disabled]="busy()">{{ t.add }}</button>
        </form>
        @if (addError(); as text) {
          <p class="error" role="alert">{{ text }}</p>
        }
      }
      @if (loadFailed()) {
        @if (!authUnavailable()) {
          <p class="error" role="alert">{{ somethingWrong }}</p>
        }
      } @else if (loaded() && notes().length === 0) {
        <p class="status">{{ t.empty }}</p>
      }
      <ul class="plain">
        @for (note of notes(); track note.id) {
          <li class="note">
            <p>{{ note.text }}</p>
            <small class="status">{{ note.created_at | date: 'medium' }}</small>
          </li>
        }
      </ul>
      @if (deleting() && me()?.org_kind === 'personal') {
        <section data-testid="delete-section">
          <h2>{{ t.deleteAccount }}</h2>
          <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
          @if (me()?.has_password !== false) {
            <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
              <label for="delete-password">{{ password }}</label>
              <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
              <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
            </form>
          } @else if (reauthTicket() === null) {
            <!-- Spec 0011: no password to ask for; the provider confirms. Nothing is deleted before the person comes back. -->
            <app-provider-buttons [providers]="me()?.linked_providers ?? []" action="confirm" [reauth]="true" [separator]="false" />
            <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
          } @else {
            <button type="button" data-testid="delete-confirm" [disabled]="deleteBusy()" (click)="deleteWithTicket()">{{ t.deleteConfirm }}</button>
            <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
          }
          @if (deleteError(); as text) {
            <p class="error" role="alert" data-testid="delete-error">{{ text }}</p>
          }
        </section>
      }
    </main>
  `,
})
export class NotesPage implements OnInit {
  protected readonly t = texts.notes;
  protected readonly somethingWrong = texts.common.somethingWrong;
  protected readonly password = texts.common.password;
  protected readonly maxCharacters = MAX_NOTE_CHARACTERS;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly me = this.auth.me;
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
  /** A company member who manages members gets the way to the team screen (spec 0010). */
  protected readonly canManageMembers = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  protected readonly deleteForm = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly deleting = signal(false);
  protected readonly deleteBusy = signal(false);
  protected readonly deleteError = signal<string | null>(null);
  // The product's notes of this person are deleted (spec 0010): the list is empty and nothing is added until the person cancels.
  protected readonly notesGone = signal(false);
  // The account turned out to belong to a company while it was being deleted: the page says so, once.
  protected readonly moved = signal(false);
  // Spec 0011: the ticket of a provider confirmation. Its arrival opens the section ready to confirm.
  protected readonly reauthTicket = signal<string | null>(reauthTicketOf(this.router));
  protected readonly form = inject(NonNullableFormBuilder).group({
    text: ['', [Validators.required, Validators.maxLength(MAX_NOTE_CHARACTERS)]],
  });
  protected readonly notes = signal<Note[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadFailed = signal(false);
  // The list failed because of auth_unavailable: the bar of the interceptor says it, not this screen.
  protected readonly authUnavailable = signal(false);
  protected readonly addError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly signingOut = signal(false);
  // The notes this page added, newest first. Whatever a list answer says, these are shown: the service has them.
  private added: Note[] = [];

  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
    if (this.reauthTicket() !== null) {
      this.deleting.set(true);
      this.focusDeleteSection();
      void this.keepTicketOutOfHistory();
    }
  }

  protected async add(): Promise<void> {
    const text = this.form.controls.text.value.trim();
    if (this.busy() || text === '') {
      return;
    }
    if (this.form.invalid) {
      // Too long (the field stops typing at the limit, so only a pasted or scripted value gets here): say the limit, send nothing.
      // A retry would not help, so this is not the "try again" text.
      this.addError.set(this.t.tooLong(MAX_NOTE_CHARACTERS));
      return;
    }
    this.busy.set(true);
    this.addError.set(null);
    try {
      const note = await firstValueFrom(this.http.post<unknown>('/api/notes', { text }));
      if (isNote(note)) {
        this.added = [note, ...this.added];
        this.notes.update((list) => [note, ...list]);
      } else {
        // The service said yes but the body is not a note: it was saved, so ask for the list instead of guessing.
        void this.load();
      }
      this.form.reset();
    } catch (error) {
      // auth_unavailable: the bar says it. What was typed stays, so the person can send it again.
      this.addError.set(isAuthUnavailable(error) ? null : this.somethingWrong);
    } finally {
      this.busy.set(false);
    }
  }

  protected async signOut(): Promise<void> {
    if (this.signingOut()) {
      return;
    }
    this.signingOut.set(true);
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  protected askToDelete(): void {
    if (this.deleteBusy()) {
      return;
    }
    this.deleteForm.reset();
    this.deleteError.set(null);
    this.moved.set(false);
    this.deleting.set(true);
    // The form opens below the notes, far from the button: the keyboard and a screen reader follow it there.
    this.focusDeleteSection();
  }

  /** The person keeps the account, so the notes can be added again (the ones that were deleted stay deleted). */
  protected cancelDelete(): void {
    this.deleting.set(false);
    this.notesGone.set(false);
    this.reauthTicket.set(null);
  }

  /** Spec 0011: "Delete my account" after a provider confirmation. The notes first, then the account, as with a password. */
  protected async deleteWithTicket(): Promise<void> {
    const ticket = this.reauthTicket();
    if (this.deleteBusy() || ticket === null) {
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
      this.deleteError.set(this.t.passwordRequired);
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
    // The notes are gone: the page shows it, whatever the account call answers.
    this.added = [];
    this.notes.set([]);
    this.loadFailed.set(false);
    this.authUnavailable.set(false);
    this.loaded.set(true);
    this.notesGone.set(true);
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
    if (result.failure.kind === 'password_required' || result.failure.kind === 'reauth_required') {
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
      // Nothing to delete here any more: the page is the one of the company the account belongs to now.
    } else {
      this.deleteError.set(`${this.somethingWrong} ${this.t.notesGone}`);
    }
    this.deleteBusy.set(false);
  }

  /**
   * True when the person turns out to belong to a company (an invitation was accepted meanwhile, so Auth-Core answered permissions_changed
   * and then forbidden). The delete state is reset, one sentence says what happened, and the notes of the current space are read.
   */
  private async leftThePrivateSpace(): Promise<boolean> {
    await this.auth.loadMe();
    const person = this.me();
    if (person === null || person.org_kind === 'personal') {
      return false;
    }
    this.deleting.set(false);
    this.deleteError.set(null);
    this.notesGone.set(false);
    this.moved.set(true);
    void this.load();
    return true;
  }

  private async load(): Promise<void> {
    try {
      const list = await firstValueFrom(this.http.get<unknown>('/api/notes'));
      if (this.notesGone()) {
        return; // an answer that was asked for before the deletion does not bring the notes back
      }
      if (!Array.isArray(list) || !list.every(isNote)) {
        throw new Error('not a list of notes');
      }
      // An answer that was asked for before a note was added does not take that note away.
      const listed = new Set(list.map((note) => note.id));
      this.notes.set([...this.added.filter((note) => !listed.has(note.id)), ...list]);
      this.loadFailed.set(false);
      this.authUnavailable.set(false);
      this.loaded.set(true);
    } catch (error) {
      if (this.notesGone()) {
        return;
      }
      // Nothing of an older list is kept next to the message, only what this page added itself.
      this.notes.set(this.added);
      this.loadFailed.set(true);
      this.authUnavailable.set(isAuthUnavailable(error));
    }
  }

  /**
   * The keyboard and a screen reader follow the section that opens below the notes: to the password field, else the first "Confirm with ..."
   * link (spec 0011), else "Delete my account" (back from the provider with the ticket).
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

  /**
   * The ticket came in the history entry of this screen (the navigation state of /provider-return). It lives on in this component's memory
   * only: the entry is replaced, same address, with no state, so a reload or a step back does not find it in `history.state`.
   */
  private async keepTicketOutOfHistory(): Promise<void> {
    // After the navigation that is building this screen.
    await Promise.resolve();
    try {
      await this.router.navigateByUrl(this.router.url, { replaceUrl: true, onSameUrlNavigation: 'reload' });
    } catch {
      // the entry keeps the state; the ticket is still only good for a few minutes and once
    }
  }
}
