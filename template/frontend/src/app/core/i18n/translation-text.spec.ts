import { textAt } from './translation-text';

describe('textAt', () => {
  const texts = { plain: 'Plain', actions: { user: { created: 'Created user ' } } };

  it('reads a top-level text', () => {
    expect(textAt(texts, 'plain')).toBe('Plain');
  });

  // Transloco 把带点号的词条键展开成嵌套对象：按整串键取会落空，列表会退回显示裸码
  it('walks dotted paths through nested objects', () => {
    expect(textAt(texts, 'actions.user.created')).toBe('Created user ');
  });

  it('returns undefined for a missing path or a non-text node', () => {
    expect(textAt(texts, 'actions.user.deleted')).toBeUndefined();
    expect(textAt(texts, 'actions.user')).toBeUndefined();
    expect(textAt({}, 'plain')).toBeUndefined();
  });
});
