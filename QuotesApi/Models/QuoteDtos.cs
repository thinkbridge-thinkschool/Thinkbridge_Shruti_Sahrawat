using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Models;

public class CreateQuoteRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Author { get; set; } = string.Empty;

    [Required, StringLength(1000, MinimumLength = 1)]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A quote as the client sees it.
/// </summary>
/// <param name="OwnerId">
/// Who created it, or null for a quote that predates accounts. Published so
/// the UI can tell an admin - who sees everyone's quotes - which rows are
/// their own. It is not what authorises anything: the server re-checks
/// ownership on every read and delete, because a client deciding what it is
/// allowed to do is a client that can decide differently.
/// </param>
public record QuoteResponse(int Id, string Author, string Text, DateTime CreatedAt, int? OwnerId)
{
    public static QuoteResponse FromEntity(Quote q) => new(q.Id, q.Author, q.Text, q.CreatedAt, q.OwnerId);
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int TotalCount);