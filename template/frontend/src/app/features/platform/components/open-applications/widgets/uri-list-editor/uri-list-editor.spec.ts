import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UriListEditor } from './uri-list-editor';

describe('UriListEditor', () => {
  function createEditor(uris: string[] = []): ComponentFixture<UriListEditor> {
    const fixture = TestBed.createComponent(UriListEditor);
    fixture.componentRef.setInput('inputId', 'redirect-uri');
    fixture.componentRef.setInput('label', 'Redirect URI');
    fixture.componentRef.setInput('addLabel', 'Add');
    fixture.componentRef.setInput('invalidText', 'Invalid URI');
    fixture.componentRef.setInput('emptyText', 'No URIs');
    fixture.componentRef.setInput('uris', uris);
    fixture.detectChanges();
    return fixture;
  }

  function enterValue(fixture: ComponentFixture<UriListEditor>, value: string): void {
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [UriListEditor] }));

  it('emits a trimmed URI when a valid value is added', () => {
    const fixture = createEditor();
    const emitted: string[] = [];
    fixture.componentInstance.add.subscribe((uri) => emitted.push(uri));

    enterValue(fixture, '  https://example.com/callback  ');
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    expect(emitted).toEqual(['https://example.com/callback']);
  });

  it('disables the add button for empty, malformed, and fragment values', () => {
    const fixture = createEditor();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;

    expect(button.disabled).toBe(true);

    enterValue(fixture, 'not-a-uri');
    expect(button.disabled).toBe(true);

    enterValue(fixture, 'https://example.com/callback#fragment');
    expect(button.disabled).toBe(true);

    enterValue(fixture, 'https://example.com/callback');
    expect(button.disabled).toBe(false);
  });

  it('emits the removed URI when its chip button is clicked', () => {
    const fixture = createEditor(['https://one.example/callback', 'https://two.example/callback']);
    let removed = '';
    fixture.componentInstance.remove.subscribe((uri) => (removed = uri));

    const removeButton = fixture.nativeElement.querySelector(
      'button[aria-label="Remove: https://one.example/callback"]',
    ) as HTMLButtonElement;
    removeButton.click();

    expect(removed).toBe('https://one.example/callback');
  });
});
