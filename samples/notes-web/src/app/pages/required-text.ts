import { AbstractControl, ValidationErrors } from '@angular/forms';

/** Like Validators.required, but text of only spaces is empty too: the screens trim it, and would send an empty address. */
export function requiredText(control: AbstractControl): ValidationErrors | null {
  return typeof control.value === 'string' && control.value.trim() !== '' ? null : { required: true };
}
