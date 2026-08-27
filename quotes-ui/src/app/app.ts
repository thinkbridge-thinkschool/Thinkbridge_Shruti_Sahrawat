import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Root shell. Standalone, so it declares what it uses in `imports` and there
 * is no NgModule anywhere in this application.
 *
 * Everything that used to be composed here directly — QuotesList, QuoteForm
 * and QuoteDetail all rendered at once inside a two-pane `.shell` grid — is
 * now reached through the router instead, one lazy chunk per route (see
 * `app.routes.ts`). That two-pane layout doesn't carry over: list and detail
 * are two different URLs now, not two panes of one page, which is what
 * makes a real View Transition *between* them possible in the first place —
 * transitioning something that was never not on screen isn't a transition.
 * `app.css`'s `.page` wrapper keeps a consistent max-width and padding
 * across every routed page without each one repeating it.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.css',
  template: `
    <div class="page">
      <router-outlet />
    </div>
  `,
})
export class App {}
