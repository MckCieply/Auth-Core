import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';

interface Note {
  id: string;
  text: string;
  author_sub: string;
  created_at: string;
}

const MAX_NOTE_CHARACTERS = 1000;

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
  imports: [ReactiveFormsModule, DatePipe],
  template: `
    <main class="wide">
      <header class="top">
        @if (me(); as person) {
          <p class="who">
            <strong data-testid="me-email">{{ person.email }}</strong>
            <span data-testid="me-company">{{ person.org_name }}</span>
            <span data-testid="me-role">{{ person.roles.join(', ') }}</span>
          </p>
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
      </header>
      <h1>{{ t.title }}</h1>
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
        <p class="error" role="alert">{{ somethingWrong }}</p>
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
  protected readonly somethingWrong = texts.common.somethingWrong;
  protected readonly maxCharacters = MAX_NOTE_CHARACTERS;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly me = this.auth.me;
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
  protected readonly form = inject(NonNullableFormBuilder).group({
    text: ['', [Validators.required, Validators.maxLength(MAX_NOTE_CHARACTERS)]],
  });
  protected readonly notes = signal<Note[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadFailed = signal(false);
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
    } catch {
      this.addError.set(this.somethingWrong);
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

  private async load(): Promise<void> {
    try {
      const list = await firstValueFrom(this.http.get<unknown>('/api/notes'));
      if (!Array.isArray(list) || !list.every(isNote)) {
        throw new Error('not a list of notes');
      }
      // An answer that was asked for before a note was added does not take that note away.
      const listed = new Set(list.map((note) => note.id));
      this.notes.set([...this.added.filter((note) => !listed.has(note.id)), ...list]);
      this.loadFailed.set(false);
      this.loaded.set(true);
    } catch {
      // Nothing of an older list is kept next to the message, only what this page added itself.
      this.notes.set(this.added);
      this.loadFailed.set(true);
    }
  }
}
