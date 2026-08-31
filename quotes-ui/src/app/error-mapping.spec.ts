import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { AppError, MAP_ERRORS, errorMappingInterceptor } from './error-mapping';

describe('errorMappingInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  const mapped = () => new HttpContext().set(MAP_ERRORS, true);

  it('passes an unopted-in request through untouched, even on a 500', async () => {
    const pending = firstValueFrom(http.get<never>('/api/quotes?page=1&size=10')).catch((e) => e);
    httpMock.expectOne('/api/quotes?page=1&size=10').flush(null, { status: 500, statusText: 'Server Error' });

    const error = await pending;
    // Still the raw HttpErrorResponse — httpResource's own statusCode()
    // depends on that shape, unchanged, for every request that does not
    // opt in.
    expect(error.status).toBe(500);
    expect(error.constructor.name).toBe('HttpErrorResponse');
  });

  it('maps a 400 ValidationProblemDetails to a validation AppError, keyed the way the API actually returns it', async () => {
    const pending = firstValueFrom(
      http.post<never>('/api/quotes', { author: '', text: '' }, { context: mapped() }),
    ).catch((e: AppError) => e);

    httpMock.expectOne('/api/quotes').flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { Author: ['The Author field is required.'], Text: ['The Text field is required.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const error = await pending;
    expect(error.kind).toBe('validation');
    if (error.kind !== 'validation') throw new Error('expected a validation error');
    expect(error.fieldErrors['Author']).toContain('The Author field is required.');
    expect(error.message).toBe('Please fix the highlighted fields and try again.');
  });

  it('maps a 403 plain ProblemDetails to a forbidden AppError using its detail', async () => {
    const pending = firstValueFrom(http.delete<never>('/api/quotes/1', { context: mapped() })).catch(
      (e: AppError) => e,
    );

    httpMock.expectOne('/api/quotes/1').flush(
      { type: 'about:blank', title: 'Not your quote', status: 403, detail: 'You can only delete quotes you added yourself.' },
      { status: 403, statusText: 'Forbidden' },
    );

    const error = await pending;
    expect(error.kind).toBe('forbidden');
    if (error.kind !== 'forbidden') throw new Error('expected a forbidden error');
    expect(error.message).toBe('You can only delete quotes you added yourself.');
  });

  it('maps a 404 plain ProblemDetails to a notFound AppError using its detail, not a field-errors dictionary that was never there', async () => {
    const pending = firstValueFrom(http.get<never>('/api/quotes/999999', { context: mapped() })).catch(
      (e: AppError) => e,
    );

    httpMock.expectOne('/api/quotes/999999').flush(
      { type: 'about:blank', title: 'Quote not found', status: 404, detail: 'No quote with id 999999.' },
      { status: 404, statusText: 'Not Found' },
    );

    const error = await pending;
    expect(error.kind).toBe('notFound');
    if (error.kind !== 'notFound') throw new Error('expected a notFound error');
    expect(error.message).toBe('No quote with id 999999.');
  });

  it('maps a 5xx to a server AppError with a friendly, non-technical message', async () => {
    const pending = firstValueFrom(http.get<never>('/api/quotes/1', { context: mapped() })).catch(
      (e: AppError) => e,
    );

    httpMock.expectOne('/api/quotes/1').flush(null, { status: 503, statusText: 'Service Unavailable' });

    const error = await pending;
    expect(error.kind).toBe('server');
    if (error.kind !== 'server') throw new Error('expected a server error');
    expect(error.statusCode).toBe(503);
    expect(error.message).not.toContain('503');
  });

  it('maps a status-0 network failure to a network AppError, distinct from a 5xx', async () => {
    const pending = firstValueFrom(http.get<never>('/api/quotes/1', { context: mapped() })).catch(
      (e: AppError) => e,
    );

    httpMock.expectOne('/api/quotes/1').error(new ProgressEvent('error'), { status: 0 });

    const error = await pending;
    expect(error.kind).toBe('network');
  });
});
