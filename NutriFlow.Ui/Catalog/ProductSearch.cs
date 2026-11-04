using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Ui.Catalog;

// the live search of the catalog (SearchProductsAsync in MainPage.Products.cs): the local search is fast, so the
// results follow typing after a short pause instead of a search button, and only the answer to the last query is shown
public sealed class ProductSearch : IDisposable
{
    private readonly LocalMealClient _meals;
    private readonly Guid _profileId;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _search;

    public ProductSearch(LocalMealClient meals, Guid profileId)
        : this(meals, profileId, TimeSpan.FromMilliseconds(250))
    {
    }

    internal ProductSearch(LocalMealClient meals, Guid profileId, TimeSpan delay)
    {
        _meals = meals;
        _profileId = profileId;
        _delay = delay;
    }

    public string Query { get; private set; } = "";

    // null until the first answer
    public IReadOnlyList<ProductResponse>? Results { get; private set; }

    // the query the shown results answer; it lags behind Query while the next search waits
    public string ResultsQuery { get; private set; } = "";

    public bool Failed { get; private set; }

    public event Action? Changed;

    public void SetQuery(string? text)
    {
        Query = text ?? "";
        _ = SearchAsync(immediate: false);
    }

    public Task SearchNowAsync() => SearchAsync(immediate: true);

    private async Task SearchAsync(bool immediate)
    {
        _search?.Cancel();
        CancellationTokenSource search = new();
        _search = search;
        string query = Query.Trim();
        try
        {
            if (!immediate)
            {
                await Task.Delay(_delay, search.Token);
            }

            IReadOnlyList<ProductResponse> products = await _meals.SearchProductsAsync(_profileId, query, search.Token);
            if (ReferenceEquals(_search, search))
            {
                Results = products;
                ResultsQuery = query;
                Failed = false;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_search, search))
            {
                UiLog.Error(exception);
                Failed = true;
                Changed?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _search?.Cancel();
        _search = null;
    }
}
