import { ChangeDetectionStrategy, Component } from '@angular/core';
import { QuotesList } from './quotes-list';
import { QuoteDetail } from './quote-detail';

/**
 * Root component. Standalone, so it declares what it uses in `imports` and
 * there is no NgModule anywhere in this application.
 */
@Component({
  selector: 'app-root',
  imports: [QuotesList, QuoteDetail],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-quotes-list />
    <app-quote-detail />
  `,
})
export class App {}
