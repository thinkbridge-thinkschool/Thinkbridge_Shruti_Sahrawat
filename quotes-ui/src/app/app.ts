import { ChangeDetectionStrategy, Component } from '@angular/core';
import { QuotesList } from './quotes-list';

/**
 * Root component. Standalone, so it declares what it uses in `imports` and
 * there is no NgModule anywhere in this application.
 */
@Component({
  selector: 'app-root',
  imports: [QuotesList],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<app-quotes-list />`,
})
export class App {}
