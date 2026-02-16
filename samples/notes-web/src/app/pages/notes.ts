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
        <button type="button" data-testid="sign-out" (click)="signOut()">{{ t.signOut }}</button>
      </header>
      <h1>{{ t.title }}</h1>
      @if (canWrite()) {
        <form [formGroup]="form" (ngSubmit)="add()" novalidate>
          <label for="text">{{ t.newNote }}</label>
          <textarea id="text" rows="3" formControlName="text" maxlength="1000"></textarea>
          <button type="submit" [disabled]="busy()">{{ t.add }}</button>
        </form>
        @if (addFailed()) {
          <p class="error" role="alert">{{ t.addFailed }}</p>
        }
      }
      @if (loadFailed()) {
        <p class="error" role="alert">{{ t.loadFailed }}</p>
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
  protected readonly addFailed = signal(false);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
  }

  protected async add(): Promise<void> {
    const text = this.form.controls.text.value.trim();
    if (this.busy() || text === '' || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.addFailed.set(false);
    try {
      const note = await firstValueFrom(this.http.post<Note>('/api/notes', { text }));
      this.notes.update((list) => [note, ...list]);
      this.form.reset();
    } catch {
      this.addFailed.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  protected async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  private async load(): Promise<void> {
    try {
      const list = await firstValueFrom(this.http.get<Note[]>('/api/notes'));
      if (!Array.isArray(list)) {
        throw new Error('not a list');
      }
      this.notes.set(list);
      this.loaded.set(true);
    } catch {
      this.loadFailed.set(true);
    }
  }
}
