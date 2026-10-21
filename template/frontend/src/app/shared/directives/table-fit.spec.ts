import { ChangeDetectionStrategy, Component, computed, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { TableFit } from './table-fit';
import { TableViewport } from '../utils/table-column-meta';

/** 每档显示的列数：模拟优先级折叠，前四列是 tablet 档仍显示的列。 */
const COLUMNS: Record<TableViewport, number> = { desktop: 6, tablet: 4, mobile: 2 };

@Component({
  imports: [TableFit],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div [appTableFit]="ceiling()" [appTableFitContent]="data()" [style.width.px]="width()">
      @defer (when shown()) {
        <div style="overflow-x: auto">
          <table style="width: 100%; border-collapse: collapse">
            <tr>
              @for (column of columns(); track column) {
                <td
                  [style.min-width.px]="
                    (column < 4 ? data().visible : data().hidden) - narrowedBy()
                  "
                  style="padding: 0"
                >
                  x
                </td>
              }
            </tr>
          </table>
        </div>
      }
    </div>
  `,
})
class Host {
  readonly ceiling = signal<TableViewport>('desktop');
  readonly width = signal(1400);
  /** 表格数据：各列内容需要的宽度随数据变化，前四列与被收起的后两列分开给。 */
  readonly data = signal({ visible: 200, hidden: 200 });
  readonly shown = signal(true);
  /** 数据不变、只是渲染出的文字变窄（例如换了界面语言）。 */
  readonly narrowedBy = signal(0);
  readonly fit = viewChild.required(TableFit);
  readonly columns = computed(() =>
    Array.from({ length: COLUMNS[this.fit().level()] }, (_, i) => i),
  );
}

/**
 * 真实布局下的折叠档位：Vitest 浏览器模式有真实的 ResizeObserver 与排版，
 * 这里断言的是"放不下就降、事实一变就从上限重来"，而不是某个固定断点。
 */
describe('TableFit', () => {
  const lang = document.documentElement.lang;

  afterEach(() => (document.documentElement.lang = lang));

  /** 等首轮测量落定：ResizeObserver 的第一次回调晚于首帧，过早改状态会和它混在一起。 */
  async function settle(): Promise<void> {
    for (let frame = 0; frame < 3; frame++) {
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    }
  }

  function create(setup: (host: Host) => void = () => undefined): Host {
    const fixture = TestBed.createComponent(Host);
    setup(fixture.componentInstance);
    fixture.autoDetectChanges();
    return fixture.componentInstance;
  }

  it('keeps every column when the container is wide enough', async () => {
    const host = create();

    await expect.poll(() => host.fit().level()).toBe('desktop');
  });

  it('folds down a level at a time until the columns fit', async () => {
    const host = create();
    await expect.poll(() => host.fit().level()).toBe('desktop');

    host.width.set(900);
    await expect.poll(() => host.fit().level()).toBe('tablet');

    host.width.set(500);
    await expect.poll(() => host.fit().level()).toBe('mobile');
  });

  it('restores the wider level once the container grows again', async () => {
    const host = create();
    await expect.poll(() => host.fit().level()).toBe('desktop');
    host.width.set(900);
    await expect.poll(() => host.fit().level()).toBe('tablet');

    host.width.set(1300);
    await expect.poll(() => host.fit().level()).toBe('desktop');
  });

  it('never goes wider than the viewport ceiling', async () => {
    const host = create();
    host.ceiling.set('mobile');

    await expect.poll(() => host.fit().level()).toBe('mobile');
  });

  it('measures a table that appears after the container was observed', async () => {
    // 平台表格都在 @defer 里：容器先渲染、表格后出现，出现那一刻也要检查
    const host = create((h) => {
      h.shown.set(false);
      h.width.set(900);
    });

    host.shown.set(true);
    await expect.poll(() => host.fit().level()).toBe('tablet');
  });

  it('folds when new data is wider while the container stays the same', async () => {
    const host = create((h) => {
      h.width.set(900);
      h.data.set({ visible: 100, hidden: 100 });
    });
    await expect.poll(() => host.fit().level()).toBe('desktop');

    host.data.set({ visible: 250, hidden: 250 });
    await expect.poll(() => host.fit().level()).toBe('mobile');
  });

  it('unfolds when only a collapsed column gets shorter with new data', async () => {
    // 被收起的列不在页面上，量不到；换了数据就得从上限重新判断
    const host = create((h) => {
      h.width.set(900);
      h.data.set({ visible: 100, hidden: 400 });
    });
    await expect.poll(() => host.fit().level()).toBe('tablet');
    await settle();

    host.data.set({ visible: 100, hidden: 100 });
    await expect.poll(() => host.fit().level()).toBe('desktop');
  });

  it('re-evaluates after a language switch', async () => {
    const host = create((h) => h.width.set(900));
    await expect.poll(() => host.fit().level()).toBe('tablet');
    await settle();

    // 数据没变，换语言后文字宽度变了（含被收起的列）；语言本身就是重新判断的依据
    document.documentElement.lang = 'xx-test';
    host.narrowedBy.set(100);
    await expect.poll(() => host.fit().level()).toBe('desktop');
  });

  it('reaches a level it never rendered after opening narrow and widening', async () => {
    // 窄屏首开时 desktop 从未渲染过，放大后仍要能升上去
    const host = create((h) => {
      h.ceiling.set('tablet');
      h.width.set(700);
    });
    await expect.poll(() => host.fit().level()).toBe('mobile');

    host.ceiling.set('desktop');
    host.width.set(1400);
    await expect.poll(() => host.fit().level()).toBe('desktop');
  });

  it('recovers after the viewport passes through the mobile ceiling and widens again', async () => {
    const host = create((h) => {
      h.ceiling.set('tablet');
      h.width.set(700);
    });
    await expect.poll(() => host.fit().level()).toBe('mobile');

    host.ceiling.set('mobile');
    host.width.set(500);
    await expect.poll(() => host.fit().level()).toBe('mobile');

    host.ceiling.set('desktop');
    host.width.set(1400);
    await expect.poll(() => host.fit().level()).toBe('desktop');
  });
});
