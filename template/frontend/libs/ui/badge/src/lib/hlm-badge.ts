import { Directive, input } from '@angular/core';
import { classes } from '@spartan-ng/helm/utils';
import { type VariantProps, cva } from 'class-variance-authority';

const badgeVariants = cva(
  'h-5 gap-1 rounded-4xl border border-transparent px-2 py-0.5 text-xs font-medium transition-all has-data-[icon=inline-end]:pe-1.5 has-data-[icon=inline-start]:ps-1.5 [&>ng-icon]:text-[length:--spacing(3)] group/badge focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 inline-flex w-fit shrink-0 items-center justify-center overflow-hidden whitespace-nowrap focus-visible:ring-[3px] [&>ng-icon]:pointer-events-none',
  {
    variants: {
      variant: {
        default: 'bg-primary text-primary-foreground [a]:hover:bg-primary/80',
        secondary: 'bg-secondary text-secondary-foreground [a]:hover:bg-secondary/80',
        destructive:
          'bg-destructive/10 [a]:hover:bg-destructive/20 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40 text-destructive dark:bg-destructive/10',
        // success 与 destructive 同构：淡色底 + 同源文字色。收成 variant 而不是让页面各写一串
        // 工具类——对比度规则（见 styles.css：alpha 上限 0.25）只有集中在这里才改得动一处生效
        success:
          'bg-success/10 [a]:hover:bg-success/20 focus-visible:ring-success/20 dark:focus-visible:ring-success/40 text-success border-success/30',
        // 待处理、需要关注：与 success 同构，淡底取同一 alpha 上限
        warning:
          'bg-warning/10 [a]:hover:bg-warning/20 focus-visible:ring-warning/20 dark:focus-visible:ring-warning/40 text-warning border-warning/30',
        outline: 'border-border text-foreground [a]:hover:bg-muted [a]:hover:text-muted-foreground',
        ghost: 'hover:bg-muted hover:text-muted-foreground dark:hover:bg-muted/50',
        link: 'text-primary underline-offset-4 hover:underline',
      },
      // 项目自定：常态状态（启用、成功、当前设备等）去掉底色与描边只留文字，配 appStatusDot 成"圆点 + 文字"；
      // 异常状态（已锁定、被拒绝、待验证）保留默认 tinted，一眼能从常态里跳出来。写在 cva 末尾，合并时压过变体的底色。
      // 只校正过 success / secondary 的非交互状态标签：其他变体的 dark: 底色、链接悬停底色与 default 的文字色不处理
      appearance: {
        tinted: '',
        plain:
          'border-transparent bg-transparent px-0 has-data-[icon=inline-end]:pe-0 has-data-[icon=inline-start]:ps-0',
      },
    },
    compoundVariants: [
      // 停用这类"不活跃"的常态去掉底色后要比正文弱一档，否则与启用只差圆点颜色
      { variant: 'secondary', appearance: 'plain', class: 'text-muted-foreground' },
    ],
    defaultVariants: {
      variant: 'default',
      appearance: 'tinted',
    },
  },
);

export type BadgeVariants = VariantProps<typeof badgeVariants>;

@Directive({
  selector: '[hlmBadge],hlm-badge',
  host: {
    'data-slot': 'badge',
    '[attr.data-variant]': 'variant()',
    '[attr.data-appearance]': 'appearance()',
  },
})
export class HlmBadge {
  public readonly variant = input<BadgeVariants['variant']>('default');
  public readonly appearance = input<BadgeVariants['appearance']>('tinted');

  constructor() {
    classes(() => badgeVariants({ variant: this.variant(), appearance: this.appearance() }));
  }
}
