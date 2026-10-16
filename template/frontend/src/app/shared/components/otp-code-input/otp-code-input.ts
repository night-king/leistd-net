import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  effect,
  ElementRef,
  inject,
  input,
  model,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { BrnInputOtp, BrnInputOtpImports } from '@spartan-ng/brain/input-otp';
import { HlmInputOtpImports } from '@spartan-ng/helm/input-otp';

const CODE_LENGTH = 6;

/**
 * 6 位验证码输入：官方 Input OTP 的 3 + 3 格组合。内部输入框带 `autocomplete="one-time-code"`，
 * 粘贴与键入只保留数字。标签须以 `for` 关联内部输入框（`inputId`），宿主上的 aria-label 读不到；
 * 页面已有可见 `<label for>` 时不传 `label`。
 *
 * 官方组件不把 `aria-invalid`、`aria-describedby` 转发到内部输入框，由 {@link invalid} 与
 * {@link describedBy} 补上：验证码被拒时读屏能读到"无效"和就地的错误说明。
 */
@Component({
  selector: 'app-otp-code-input',
  imports: [...BrnInputOtpImports, ...HlmInputOtpImports],
  template: `
    @if (label(); as text) {
      <label class="sr-only" [for]="inputId()">{{ text }}</label>
    }
    <brn-input-otp
      hlmInputOtp
      [length]="length"
      [inputId]="inputId()"
      [value]="value()"
      [autofocus]="autofocus()"
      [disabled]="disabled()"
      [transformPaste]="digitsOnly"
      (valueChange)="onValueChange($event)"
      (completed)="completed.emit(digitsOnly($event))"
    >
      <div hlmInputOtpGroup>
        <hlm-input-otp-slot index="0" [forceInvalid]="invalid()" />
        <hlm-input-otp-slot index="1" [forceInvalid]="invalid()" />
        <hlm-input-otp-slot index="2" [forceInvalid]="invalid()" />
      </div>
      <hlm-input-otp-separator />
      <div hlmInputOtpGroup>
        <hlm-input-otp-slot index="3" [forceInvalid]="invalid()" />
        <hlm-input-otp-slot index="4" [forceInvalid]="invalid()" />
        <hlm-input-otp-slot index="5" [forceInvalid]="invalid()" />
      </div>
    </brn-input-otp>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OtpCodeInput {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** 内部输入框的 id，供 `<label for>` 关联。 */
  readonly inputId = input.required<string>();
  /** 仅供读屏器的标签；页面上已有可见标签时留空。 */
  readonly label = input<string>('');
  readonly autofocus = input(false);
  readonly disabled = input(false);
  /** 验证码被拒：内部输入框带 `aria-invalid`，各格显示错误样式。 */
  readonly invalid = input(false);
  /** 说明与错误元素的 id（空格分隔），写到内部输入框的 `aria-describedby`。 */
  readonly describedBy = input<string | null>(null);
  /** 当前值，只含数字。 */
  readonly value = model('');
  /** 输满 6 位时触发，适合用来自动提交。 */
  readonly completed = output<string>();

  protected readonly length = CODE_LENGTH;
  private readonly otp = viewChild(BrnInputOtp);

  constructor() {
    // 调用方清空（验证码被拒）可能与本次输入落在同一轮变更检测里：模板绑定看不出值变过，
    // 官方组件仍留着旧码，下一次输满也不再触发 completed。值不一致时直接同步过去。
    effect(() => {
      const value = this.value();
      untracked(() => {
        const otp = this.otp();
        if (otp && (otp.value() ?? '') !== value) {
          otp.value.set(value);
        }
      });
    });
    // 内部输入框在官方组件的模板里，渲染之后才能写它的属性
    afterRenderEffect(() => {
      const element = this.innerInput();
      if (!element) {
        return;
      }
      setOptionalAttribute(element, 'aria-invalid', this.invalid() ? 'true' : null);
      setOptionalAttribute(element, 'aria-describedby', this.describedBy());
    });
  }

  /** 把焦点放进内部输入框（切换方式或输错清空后请本人重输时用）。 */
  focus(): void {
    this.innerInput()?.focus();
  }

  protected readonly digitsOnly = (text: string): string => text.replace(/\D/g, '');

  protected onValueChange(raw: string): void {
    this.value.set(this.digitsOnly(raw));
  }

  private innerInput(): HTMLInputElement | null {
    return this.host.nativeElement.querySelector('input');
  }
}

function setOptionalAttribute(element: HTMLElement, name: string, value: string | null): void {
  if (value) {
    element.setAttribute(name, value);
  } else {
    element.removeAttribute(name);
  }
}
