import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AccountApi } from '../account-api';
import { AuthService, errorCode } from '../auth/auth.service';
import { texts } from '../texts';

interface Note {
  id: string;
  text: string;
  author_sub: string;
  created_at: string;
}

const MAX_NOTE_CHARACTERS = 1000;
// The value of the picker's last option, "New company...": it is no company id, so it is never sent to POST /auth/switch.
const NEW_COMPANY = 'new-company';

/**
 * True for a 503 {"error":"auth_unavailable"}: the notes service cannot reach Auth-Core. The interceptor shows the bar "Try again
 * shortly." for that answer (spec 0007, session table), so the screen adds no sentence of its own next to it.
 */
function isAuthUnavailable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
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
  imports: [ReactiveFormsModule, DatePipe, RouterLink],
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
              @if (canOpenTeam()) {
                <a routerLink="/team" data-testid="team-link">{{ t.team }}</a>
              }
            }
          </p>
        }
        @if (orgs().length > 1 || (orgCreation() && orgs().length > 0)) {
          <!-- Spec 0015: the account belongs to more than one space. Choosing one moves the session there. Spec 0016: "New company..." opens a form. -->
          <label for="org-picker">{{ o.label }}</label>
          <select id="org-picker" data-testid="org-picker" [disabled]="switching() || creatingBusy()" (change)="switchTo($event)">
            @for (entry of orgs(); track entry.id) {
              <option [value]="entry.id" [selected]="entry.current">{{ entry.kind === 'personal' ? o.private : entry.name }}</option>
            }
            @if (orgCreation()) {
              <option [value]="newCompany">{{ o.newCompany }}</option>
            }
          </select>
        }
        @if (me()) {
          <a routerLink="/settings" data-testid="settings-link">{{ t.settings }}</a>
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
      </header>
      @if (creating()) {
        <form [formGroup]="companyForm" (ngSubmit)="createCompany()" novalidate data-testid="new-company-form">
          <label for="new-company-name">{{ o.newCompanyName }}</label>
          <input id="new-company-name" data-testid="new-company-name" type="text" formControlName="name" autocomplete="off" maxlength="100" />
          <button type="submit" data-testid="new-company-submit" [disabled]="creatingBusy()">{{ o.createCompany }}</button>
          <button type="button" data-testid="new-company-cancel" [disabled]="creatingBusy()" (click)="cancelCreate()">{{ cancel }}</button>
          @if (createError(); as text) {
            <p class="error" role="alert" data-testid="new-company-error">{{ text }}</p>
          }
        </form>
      }
      @if (creationOffNotice()) {
        <p class="status" role="status" data-testid="new-company-off">{{ o.creationOff }}</p>
      }
      <h1>{{ t.title }}</h1>
      @if (switchProblem() === 'gone') {
        <p class="status" role="status" data-testid="org-gone">{{ o.gone }}</p>
      } @else if (switchProblem() === 'changed') {
        <p class="status" role="status" data-testid="org-changed">{{ o.changed }}</p>
      } @else if (switchProblem() === 'failed') {
        <p class="error" role="alert" data-testid="org-switch-failed">{{ somethingWrong }}</p>
      }
      @if (canWrite()) {
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
    </main>
  `,
})
export class NotesPage implements OnInit {
  protected readonly t = texts.notes;
  protected readonly o = texts.orgs;
  protected readonly somethingWrong = texts.common.somethingWrong;
  protected readonly cancel = texts.common.cancel;
  protected readonly maxCharacters = MAX_NOTE_CHARACTERS;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);

  protected readonly me = this.auth.me;
  protected readonly orgs = this.auth.orgs;
  protected readonly switching = signal(false);
  // Spec 0016: GET /auth/options says `org_creation` `self-service`. Read once when the page starts; the picker of the next task uses it.
  protected readonly orgCreation = signal(false);
  protected readonly newCompany = NEW_COMPANY;
  // The small form of "New company...", the request that is on its way, the text under the form, and the notice after a switch-off.
  protected readonly creating = signal(false);
  protected readonly creatingBusy = signal(false);
  protected readonly createError = signal<string | null>(null);
  protected readonly creationOffNotice = signal(false);
  protected readonly companyForm = inject(NonNullableFormBuilder).group({ name: ['', Validators.required] });
  // The last switch that did not go through: the company is gone from the account, or the call failed.
  protected readonly switchProblem = signal<'gone' | 'changed' | 'failed' | null>(null);
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
  /** A company member who manages members gets the way to the team screen (spec 0010). */
  protected readonly canManageMembers = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  /** Spec 0012: "Delete company" is on the team screen too. */
  private readonly canDeleteCompany = computed(() => this.me()?.permissions.includes('org:delete') ?? false);
  protected readonly canOpenTeam = computed(() => this.canManageMembers() || this.canDeleteCompany());
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
  // The notes are read again whenever the company of the session changes (spec 0015); the counter is what the page has already seen.
  private seenOrgChanges = this.auth.orgChanges();
  // Numbers the list requests: only the last one is shown, so an answer for a company the person has left is dropped.
  private loadNumber = 0;

  constructor() {
    effect(() => {
      const changes = this.auth.orgChanges();
      untracked(() => this.orgChanged(changes));
    });
  }

  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
    void this.account.options().then((options) => this.orgCreation.set(options.orgCreation));
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

  /** The picker: choosing another space moves the session there. */
  protected async switchTo(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const wanted = select.value;
    const current = (): string => this.orgs().find((entry) => entry.current)?.id ?? this.me()?.org_id ?? '';
    if (wanted === NEW_COMPANY) {
      // Not a space: the select goes back to the current one and the form opens.
      select.value = current();
      this.openCreateForm();
      return;
    }
    if (this.switching() || wanted === current()) {
      select.value = current();
      return;
    }
    this.switching.set(true);
    this.switchProblem.set(null);
    try {
      const result = await this.auth.switchTo(wanted);
      if (result === 'gone' || result === 'failed') {
        this.switchProblem.set(result);
      }
    } finally {
      this.switching.set(false);
      // The select shows the space the session is in, whatever came of the choice.
      select.value = current();
    }
  }

  private openCreateForm(): void {
    if (this.creatingBusy()) {
      return;
    }
    this.companyForm.reset();
    this.createError.set(null);
    this.creationOffNotice.set(false);
    this.creating.set(true);
  }

  protected cancelCreate(): void {
    if (this.creatingBusy()) {
      return;
    }
    this.creating.set(false);
    this.createError.set(null);
  }

  /** Spec 0016: POST /auth/orgs, then the session moves into the new company. The button is disabled while the request runs. */
  protected async createCompany(): Promise<void> {
    if (this.creatingBusy()) {
      return;
    }
    const name = this.companyForm.controls.name.value;
    if (name.trim() === '') {
      this.createError.set(this.o.nameRule);
      return;
    }
    this.creatingBusy.set(true);
    this.createError.set(null);
    this.switchProblem.set(null);
    try {
      const result = await this.auth.createCompany(name);
      if (result === 'created') {
        this.creating.set(false);
      } else if (result === 'limit') {
        this.createError.set(this.o.limitReached);
      } else if (result === 'disabled') {
        this.orgCreation.set(false);
        this.creating.set(false);
        this.creationOffNotice.set(true);
      } else if (result === 'invalid') {
        this.createError.set(this.o.nameRule);
      } else if (result === 'changed') {
        // permissions_changed: the list is asked again and the page says it is up to date.
        await this.auth.loadOrgs();
        this.creating.set(false);
        this.switchProblem.set('changed');
      } else if (result === 'gone') {
        // The switch found the new company gone: `switchTo` has asked for the list again already.
        this.creating.set(false);
        this.switchProblem.set('gone');
      } else if (result === 'switch-failed') {
        // The company exists, only the switch failed (`switchTo` does not ask for the list then): the list shows it, the form is closed.
        await this.auth.loadOrgs();
        this.creating.set(false);
        this.switchProblem.set('failed');
      } else {
        this.createError.set(this.somethingWrong);
      }
    } finally {
      this.creatingBusy.set(false);
    }
  }

  /** The company of the session changed (a switch here, or one of another tab found by a refresh): nothing of the old one stays. */
  private orgChanged(changes: number): void {
    if (changes === this.seenOrgChanges) {
      return;
    }
    this.seenOrgChanges = changes;
    this.added = [];
    this.notes.set([]);
    this.addError.set(null);
    this.loaded.set(false);
    this.loadFailed.set(false);
    this.authUnavailable.set(false);
    this.switchProblem.set(null);
    void this.load();
  }

  protected async signOut(): Promise<void> {
    if (this.signingOut()) {
      return;
    }
    this.signingOut.set(true);
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  private async load(): Promise<void> {
    const mine = ++this.loadNumber;
    try {
      const list = await firstValueFrom(this.http.get<unknown>('/api/notes'));
      if (mine !== this.loadNumber) {
        return; // an answer that was asked for before a newer request is dropped
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
      if (mine !== this.loadNumber) {
        return;
      }
      // Nothing of an older list is kept next to the message, only what this page added itself.
      this.notes.set(this.added);
      this.loadFailed.set(true);
      this.authUnavailable.set(isAuthUnavailable(error));
    }
  }
}
