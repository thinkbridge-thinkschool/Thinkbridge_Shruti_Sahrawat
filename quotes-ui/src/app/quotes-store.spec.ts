import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { errorMappingInterceptor } from './error-mapping';
import { QuotesStore } from './quotes-store';
import type { Quote } from './quotes';

/**
 * Exercises QuotesStore — the signal store owning the quotes-list feature —
 * against a mocked Week-1 API, with no live backend in this environment.
 *
 * Written against the brief before the store existed, in one deliberate
 * respect: the concurrent-delete case at the bottom. Everything above it
 * describes behaviour a single-delete implementation gets right by
 * accident; that one describes what has to be true when two deletes
 * overlap, which is where the draft's rollback turned out to be wrong.
 *
 * `settle()` after every state-changing call is not incidental. An
 * httpResource's request/response handling runs through a reactive effect
 * and a promise hop, not synchronously inside the call that changed the
 * signal it depends on — so a test has to explicitly let pending
 * microtasks and effects run before asserting.
 */
describe('QuotesStore', () => {
  let store: QuotesStore;
  let httpMock: HttpTestingController;

  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    TestBed.tick();
  }

  const quote = (id: number, author = `Author ${id}`): Quote => ({
    id,
    author,
    text: `Quote text ${id}`,
    createdAt: '2026-03-14T09:30:00',
    // The store does no ownership filtering of its own - the server returns
    // only what the caller may see - so a single fixed owner is enough here.
    ownerId: 1,
  });

  /** The real GET envelope: items/page/size/totalCount, not a bare array. */
  const page = (items: Quote[], totalCount = items.length) => ({
    items,
    page: 1,
    size: 10,
    totalCount,
  });

  /** Matches the list request whatever page/size it currently carries. */
  function listRequest(): TestRequest {
    return httpMock.expectOne((req) => req.url.startsWith('/api/quotes?'));
  }

  /** Flushes the list request that fires on store construction. */
  async function loadInitial(items: Quote[], totalCount = items.length): Promise<void> {
    listRequest().flush(page(items, totalCount));
    await settle();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        // errorMappingInterceptor is wired in deliberately, not left to its
        // own spec. `deleteQuote` opts its DELETE into MAP_ERRORS and
        // branches on `AppError.kind === 'notFound'` — without the
        // interceptor running, the thrown value is a raw
        // HttpErrorResponse whose `kind` is undefined, every failure looks
        // alike, and the 404 case silently takes the rollback branch.
        // Testing the store without the interceptor its production
        // app.config.ts always runs alongside would be testing a shape the
        // real app never produces. (Found the hard way: this spec's first
        // run failed the 404 case for exactly this reason.)
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    store = TestBed.inject(QuotesStore);
    httpMock = TestBed.inject(HttpTestingController);
    await settle();
  });

  afterEach(() => httpMock.verify({ ignoreCancelled: true }));

  // ---- the read path: the four states the list can be in ----------------

  describe('list states', () => {
    it('is loading before the first response arrives', async () => {
      expect(store.listState()).toBe('loading');
      await loadInitial([quote(1)]);
    });

    it('is ready with rows once the response arrives', async () => {
      await loadInitial([quote(1), quote(2)]);

      expect(store.listState()).toBe('ready');
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([1, 2]);
      expect(store.totalCount()).toBe(2);
    });

    it('is no-data when the API returns an empty page', async () => {
      await loadInitial([], 0);

      expect(store.listState()).toBe('no-data');
    });

    it('is error when the list request fails', async () => {
      listRequest().flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
      await settle();

      expect(store.listState()).toBe('error');
    });

    it('is no-matches when the filter excludes every row — distinct from no-data', async () => {
      await loadInitial([quote(1, 'Ada Lovelace'), quote(2, 'Alan Turing')]);

      store.setAuthorFilter('Grace');
      await settle();

      // Different state, different words on screen, different recovery
      // action. Collapsing these two into one isEmpty is how you get a
      // "clear filter" button on a screen with no filter applied.
      expect(store.listState()).toBe('no-matches');
      expect(store.visibleQuotes()).toEqual([]);
    });

    it('filters client-side over the current page and issues no request', async () => {
      await loadInitial([quote(1, 'Ada Lovelace'), quote(2, 'Alan Turing')]);

      store.setAuthorFilter('ada');
      await settle();

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([1]);
      // afterEach's httpMock.verify() is the assertion that matters here:
      // a keystroke that reached the server would leave an unflushed
      // request behind. The filter narrows rows already in hand.
    });
  });

  // ---- the write path: optimistic delete --------------------------------

  describe('deleteQuote', () => {
    it('removes the row immediately, before the server answers', async () => {
      await loadInitial([quote(1), quote(2)], 2);

      store.deleteQuote(1);
      await settle();

      // The row is gone from the list the instant the user clicks — this
      // is the whole point of an optimistic update, and it must be true
      // while the DELETE is still in flight.
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);

      httpMock.expectOne({ method: 'DELETE', url: '/api/quotes/1' }).flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      listRequest().flush(page([quote(2)], 1));
      await settle();
    });

    it('decrements the visible total while the delete is in flight', async () => {
      await loadInitial([quote(1), quote(2)], 42);

      expect(store.totalCount()).toBe(42);

      store.deleteQuote(1);
      await settle();

      // The pager is derived from this number. Leaving it at 42 while a
      // row is visibly gone means the count and the rows on screen
      // disagree, which is the kind of thing a reviewer notices
      // immediately and a test has to pin.
      expect(store.totalCount()).toBe(41);

      httpMock.expectOne({ method: 'DELETE', url: '/api/quotes/1' }).flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      listRequest().flush(page([quote(2)], 41));
      await settle();
    });

    it('keeps the row gone once the server confirms with 204', async () => {
      await loadInitial([quote(1), quote(2)], 2);

      store.deleteQuote(1);
      await settle();
      httpMock.expectOne({ method: 'DELETE', url: '/api/quotes/1' }).flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      // A successful delete refetches the page — the row the server no
      // longer returns simply isn't there any more.
      listRequest().flush(page([quote(2)], 1));
      await settle();

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);
      expect(store.totalCount()).toBe(1);
      expect(store.deleteError()).toBeNull();
    });

    it('puts the row back and reports the failure when the delete fails', async () => {
      await loadInitial([quote(1), quote(2)], 2);

      store.deleteQuote(1);
      await settle();
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);

      httpMock
        .expectOne({ method: 'DELETE', url: '/api/quotes/1' })
        .flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
      await settle();

      // Rolled back: the row returns, in its original position, and the
      // count goes back with it.
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([1, 2]);
      expect(store.totalCount()).toBe(2);
      expect(store.deleteError()).not.toBeNull();
    });

    it('treats a 404 on delete as the outcome the user wanted, not a rollback', async () => {
      await loadInitial([quote(1), quote(2)], 2);

      store.deleteQuote(1);
      await settle();

      // A 404 here means the quote is already gone — someone else deleted
      // it, or this is a retry of a request that already succeeded. The
      // user asked for it to not exist, and it does not exist. Rolling
      // back would resurrect a row that no longer exists server-side,
      // which the very next refetch would remove again anyway — a visible
      // flicker that tells the user nothing true.
      httpMock
        .expectOne({ method: 'DELETE', url: '/api/quotes/1' })
        .flush(
          { title: 'Quote not found', status: 404, detail: 'No quote with id 1.' },
          { status: 404, statusText: 'Not Found' },
        );
      await settle();

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);
      expect(store.deleteError()).toBeNull();

      listRequest().flush(page([quote(2)], 1));
      await settle();
    });

    // ---- the case the draft got wrong ----------------------------------

    it('rolls back only the delete that failed, not one that already succeeded', async () => {
      await loadInitial([quote(1), quote(2), quote(3)], 3);

      // Two deletes in quick succession — an ordinary "remove these two"
      // sequence, not a contrived timing.
      store.deleteQuote(1);
      await settle();
      store.deleteQuote(2);
      await settle();

      expect(store.visibleQuotes().map((q) => q.id)).toEqual([3]);

      const deleteOne = httpMock.expectOne({ method: 'DELETE', url: '/api/quotes/1' });
      const deleteTwo = httpMock.expectOne({ method: 'DELETE', url: '/api/quotes/2' });

      // Quote 1 is really deleted, server-side. Quote 2's delete fails.
      deleteOne.flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      deleteTwo.flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
      await settle();

      // Quote 2 comes back, because its delete failed. Quote 1 must NOT
      // come back: the server has already deleted it, and showing it again
      // is showing the user a row that does not exist. A rollback that
      // restores a whole-list snapshot taken when delete 2 started cannot
      // tell these two apart — the snapshot still contains quote 1.
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2, 3]);
      expect(store.deleteError()).not.toBeNull();

      // Delete 1's own success refetch.
      listRequest().flush(page([quote(2), quote(3)], 2));
      await settle();
      expect(store.visibleQuotes().map((q) => q.id)).toEqual([2, 3]);
    });
  });
});
