namespace NutriFlow.Infrastructure.Ai;

public delegate Task GroqRetryDelay(TimeSpan wait, CancellationToken cancellationToken);
