import { englishText } from './english-text';

describe('englishText', () => {
  const t = englishText({ greeting: 'Hello, {{name}}', plain: 'Plain' });

  it('replaces placeholders with params', () => {
    expect(t('greeting', { name: 'Ada' })).toBe('Hello, Ada');
  });

  it('returns the text as is without placeholders', () => {
    expect(t('plain')).toBe('Plain');
  });

  it('falls back to the key when the table has no entry', () => {
    expect(t('missing.key')).toBe('missing.key');
  });
});
