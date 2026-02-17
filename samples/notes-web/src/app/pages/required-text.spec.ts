import { FormControl } from '@angular/forms';
import { requiredText } from './required-text';

describe('requiredText', () => {
  it.each(['', ' ', '   ', '\t\n ', null])('is missing for %j', (value) => {
    expect(requiredText(new FormControl(value))).toEqual({ required: true });
  });

  it.each(['a', ' a ', 'user@example.test'])('is present for %j', (value) => {
    expect(requiredText(new FormControl(value))).toBeNull();
  });
});
