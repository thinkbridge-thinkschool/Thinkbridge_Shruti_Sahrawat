import { ChangeDetectionStrategy, Component } from '@angular/core';
import { QuotesList } from './quotes-list';
import { QuoteDetail } from './quote-detail';

/**
 * Root component. Standalone, so it declares what it uses in `imports` and
 * there is no NgModule anywhere in this application.
 *
 * Owns the page-level layout (the `.shell` grid in app.css) so neither child
 * has to know where it sits relative to the other — QuotesList and
 * QuoteDetail each still render correctly standalone, e.g. under test.
 */
@Component({
  selector: 'app-root',
  imports: [QuotesList, QuoteDetail],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.css',
  template: `
    <div class="shell">
      <app-quotes-list />
      <app-quote-detail />
    </div>
  `,
})
export class App {}
