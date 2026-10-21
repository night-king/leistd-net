import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucidePlus, lucideX } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';

/**
 * URI 列表编辑器：一个输入框 + 校验 + 添加，下方以 chip 展示已添加项并可删除。
 * 纯展示组件——所有文案由父级以输入传入（便于本地化），校验规则内置（绝对 URI、无 fragment）。
 * Redirect URIs 与 Post-Logout Redirect URIs 复用同一实现。
 */
@Component({
  selector: 'app-uri-list-editor',
  imports: [NgIcon, HlmBadge, HlmButton, HlmInput, ...HlmFieldImports],
  providers: [provideIcons({ lucidePlus, lucideX })],
  templateUrl: './uri-list-editor.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UriListEditor {
  readonly inputId = input.required<string>();
  readonly label = input('');
  readonly hint = input('');
  readonly placeholder = input('');
  readonly addLabel = input('');
  readonly invalidText = input('');
  readonly emptyText = input('');
  readonly removeLabel = input('Remove');
  readonly uris = input<string[]>([]);
  readonly badgeVariant = input<'secondary' | 'outline'>('secondary');

  readonly add = output<string>();
  readonly remove = output<string>();

  protected readonly value = signal('');
  protected readonly isInvalid = computed(() => {
    const value = this.value().trim();
    return !!value && !this.isValidUri(value);
  });

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);
  }

  protected onAdd(): void {
    const value = this.value().trim();
    if (!value || !this.isValidUri(value)) {
      return;
    }
    this.add.emit(value);
    this.value.set('');
  }

  private isValidUri(value: string): boolean {
    if (!/^[a-z][a-z0-9+.-]*:/i.test(value) || /\s/.test(value)) {
      return false;
    }

    try {
      const uri = new URL(value);
      return !!uri.protocol && !uri.hash;
    } catch {
      return false;
    }
  }
}
