using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class LocalMealClientThreadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-mobile-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 10, 12);
    private bool _retainRootOnTimeout;

    [Fact]
    public async Task DatabaseOperationRunsOutsideCallerContextAndAwaitReturnsToCaller()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Фоновая операция");
        SynchronizationContext? operationContext = null;
        int operationThread = 0;
        LocalMealClient client = new(profiles, () =>
        {
            operationContext = SynchronizationContext.Current;
            operationThread = Environment.CurrentManagedThreadId;
            return new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        });

        await RunOnUiThreadAsync(async () =>
        {
            SynchronizationContext callerContext = SynchronizationContext.Current!;
            int callerThread = Environment.CurrentManagedThreadId;

            ProductResponse? result = await client.LookupBarcodeAsync(profile.Id, "3017620422003");

            Assert.Null(result);
            Assert.NotEqual(0, operationThread);
            Assert.Null(operationContext);
            Assert.NotEqual(callerThread, operationThread);
            Assert.Same(callerContext, SynchronizationContext.Current);
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
        });
    }

    [Fact]
    public async Task PreCanceledOperationDoesNotCreateDatabaseOrNetworkClient()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Отменённая операция");
        int networkClients = 0;
        LocalMealClient client = new(profiles, () =>
        {
            Interlocked.Increment(ref networkClients);
            throw new InvalidOperationException("Network must not be used");
        });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await RunOnUiThreadAsync(async () =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetDailyProgressAsync(profile.Id, _date, cancellation.Token));
        });

        Assert.Equal(0, networkClients);
        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", profile.Id.ToString("N"))));
    }

    [Fact]
    public async Task CancellationDuringOperationReachesCallerAndAllowsNextLocalOperation()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Отмена запроса");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalMealClient client = new(profiles, () => new HttpClient(new Handler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        using CancellationTokenSource cancellation = new();

        await RunOnUiThreadAsync(async () =>
        {
            Task<ProductResponse?> pending = client.LookupBarcodeAsync(profile.Id, "3017620422003", cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                }
            }

            Assert.Empty(await client.SearchProductsAsync(profile.Id));
        });
    }

    [Fact]
    public async Task OperationExceptionReachesCallerUnchangedAndAllowsNextLocalOperation()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Ошибка запроса");
        InvalidOperationException expected = new("Synthetic operation failure");
        LocalMealClient client = new(profiles, () => throw expected);

        await RunOnUiThreadAsync(async () =>
        {
            InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.LookupBarcodeAsync(profile.Id, "3017620422003"));

            Assert.Same(expected, actual);
            Assert.Empty(await client.SearchProductsAsync(profile.Id));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualDishCopiesCallerOwnedIngredientsBeforeBackgroundWork(bool explicitSelection)
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Состав блюда");
        LocalMealClient client = new(profiles);
        ProductResponse product = new("Продукт", 100m, 10m, 5m, 15m,
            "ManualInput", "Exact", "Ручной ввод", null, null);
        await client.AddProductAsync(profile.Id, product);

        await RunOnUiThreadAsync(async () =>
        {
            MealSessionResponse session;
            if (explicitSelection)
            {
                ThreadBoundReadOnlyList<ManualIngredient> ingredients = new([new ManualIngredient(product, 100m)]);
                session = await client.CreateManualDishAsync(profile.Id, "Блюдо", ingredients, 200m, _date);
            }
            else
            {
                ThreadBoundReadOnlyList<IngredientDraft> ingredients = new([new IngredientDraft("Продукт", 100m)]);
                session = await client.CreateManualDishAsync(profile.Id, "Блюдо", ingredients, 200m, _date);
            }

            Assert.True(session.CanConfirm);
            IngredientPreviewResponse ingredient = Assert.Single(Assert.Single(session.Dishes).Ingredients);
            Assert.Equal("Продукт", ingredient.ResolvedProduct!.Name);
            Assert.Equal(100m, ingredient.WeightInGrams);
        });
    }

    [Fact]
    public async Task TextSessionCopiesCallerOwnedMessagesBeforeBackgroundWork()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Текстовый ввод");
        const string draft = """
            {"sessionState":"ReadyForReview","dishes":[{"name":"Продукт","ingredients":[{"productName":"Продукт","weightInGrams":100,"weightQuality":"Exact","removedWeightInGrams":0,"removedWeightQuality":"Exact"}],"finalWeightInGrams":100,"finalWeightQuality":"Exact","portions":[{"weightInGrams":100,"fractionOfDish":null,"weightQuality":"Exact"}]}],"clarificationQuestions":[]}
            """;
        string body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = draft }, finish_reason = "stop" } } });
        LocalMealClient client = new(profiles, () => new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }))));

        await RunOnUiThreadAsync(async () =>
        {
            ThreadBoundReadOnlyList<string> messages = new(["Съел продукт"]);
            MealSessionResponse session = await client.CreateSessionAsync(profile.Id, messages, _date,
                MealSessionPurpose.Diary, new GroqSettings("synthetic-test-key"));

            Assert.Equal("Съел продукт", Assert.Single(session.Messages));
            Assert.Equal("Продукт", Assert.Single(session.Dishes).Name);
        });
    }

    private async Task RunOnUiThreadAsync(Func<Task> operation)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Exception? failure = null;
            try
            {
                using UiSynchronizationContext context = new();
                SynchronizationContext.SetSynchronizationContext(context);
                Task pending = operation();
                _ = pending.ContinueWith(_ => context.Complete(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                context.Run();
                pending.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        })
        {
            IsBackground = true,
            Name = "NutriFlow test UI"
        };
        thread.Start();
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException) when (!completion.Task.IsCompleted)
        {
            _retainRootOnTimeout = true;
            throw;
        }
    }

    public void Dispose()
    {
        if (!_retainRootOnTimeout && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class UiSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

        public void Complete() => _queue.CompleteAdding();

        public void Run()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose() => _queue.Dispose();
    }

    private sealed class ThreadBoundReadOnlyList<T>(T[] items) : IReadOnlyList<T>
    {
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;

        public int Count
        {
            get
            {
                RequireOwnerThread();
                return items.Length;
            }
        }

        public T this[int index]
        {
            get
            {
                RequireOwnerThread();
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            RequireOwnerThread();
            return ((IEnumerable<T>)items).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread)
            {
                throw new InvalidOperationException("Caller-owned collection was accessed from another thread.");
            }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }
}
