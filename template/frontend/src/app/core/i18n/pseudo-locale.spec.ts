import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';

import {
  displayWidth,
  labelCapacity,
  pseudoCatalog,
  pseudoLocalize,
} from './pseudo-locale.testing';
import { provideTranslocoTesting } from './transloco.testing';

/** 伪语言的宽度就是布局用例施加的压力：样例把公式钉住，改数值要同时改规范（frontend-i18n.md §5）。 */
describe('pseudo locale', () => {
  it('counts wide characters as two columns and combining or format characters as none', () => {
    expect(displayWidth('Users')).toBe(5);
    // 中文「用户」、全角字母 u：东亚宽字符各占 2 列
    expect(displayWidth('\u7528\u6237')).toBe(4);
    expect(displayWidth('\uFF55')).toBe(2);
    // 泰文辅音 + 声调符号、越南文分解形式的声调：附加符号不占列
    expect(displayWidth('\u0E01\u0E48')).toBe(1);
    expect(displayWidth('a\u0301')).toBe(1);
    expect(displayWidth('a\u200Db')).toBe(2);
    expect(displayWidth('{{total}} items')).toBe(6);
  });

  it('gives short labels the 14-column floor and longer ones 1.3 times their width', () => {
    expect(labelCapacity('Add')).toBe(14);
    expect(labelCapacity('Account & security')).toBe(24);
    expect(labelCapacity('Operation records')).toBe(23);
  });

  it('fills the brackets and accents to exactly the label capacity', () => {
    expect(pseudoLocalize('Save')).toBe('[Šàṽé~~~~~~~~]');
    expect(pseudoLocalize('Operation records')).toBe('[Öþéŕàţîöñ ŕéçöŕđš~~~~]');
    for (const source of ['Save', 'Operation records', 'Account & security', '{{total}} items']) {
      expect(displayWidth(pseudoLocalize(source)), source).toBe(labelCapacity(source));
    }
  });

  it('keeps interpolations replaceable', () => {
    TestBed.configureTestingModule({ providers: provideTranslocoTesting() });
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation({ greeting: pseudoLocalize('Hello {{name}}') }, 'en');

    expect(pseudoLocalize('Hello {{name}}')).toBe('[Ĥéľľö {{name}}~~~~~~]');
    expect(transloco.translate('greeting', { name: 'Ada' })).toBe('[Ĥéľľö Ada~~~~~~]');
  });

  it('localizes every string of a catalog and keeps its shape', () => {
    expect(pseudoCatalog({ menu: { users: 'Users' }, count: 3 })).toEqual({
      menu: { users: '[Ûšéŕš~~~~~~~]' },
      count: 3,
    });
  });
});
