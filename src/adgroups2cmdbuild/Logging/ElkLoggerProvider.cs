using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AdGroups2Cmdbuild.Configuration;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Logging;

public sealed class ElkLoggerProvider : ILoggerProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ElkLoggingOptions options;
    private readonly Channel<ElkLogEvent>? channel;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task? worker;

    public ElkLoggerProvider(IOptions<ElkLoggingOptions> options)
    {
        this.options = options.Value;
        if (!this.options.IsActive())
        {
            return;
        }

        channel = Channel.CreateBounded<ElkLogEvent>(new BoundedChannelOptions(Math.Max(1, this.options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        worker = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        if (channel is null || !options.IsActive())
        {
            return NullLogger.Instance;
        }

        return new ElkLogger(categoryName, options, channel.Writer);
    }

    public void Dispose()
    {
        if (channel is null)
        {
            cancellation.Dispose();
            return;
        }

        channel.Writer.TryComplete();
        using var flush = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, options.FlushTimeoutMs)));
        try
        {
            worker?.Wait(flush.Token);
        }
        catch (OperationCanceledException)
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ProcessQueueAsync()
    {
        if (channel is null)
        {
            return;
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1, options.TimeoutMs))
        };

        await foreach (var item in channel.Reader.ReadAllAsync(cancellation.Token))
        {
            await SendAsync(httpClient, item, cancellation.Token);
        }
    }

    private async Task SendAsync(HttpClient httpClient, ElkLogEvent item, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildLogEndpoint());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey);
            }

            request.Content = new StringContent(JsonSerializer.Serialize(item, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            // Logging failures must not break the main synchronization flow.
        }
    }

    private string BuildLogEndpoint()
    {
        if (string.IsNullOrWhiteSpace(options.Index))
        {
            return options.Endpoint;
        }

        var endpoint = options.Endpoint.TrimEnd('/');
        if (endpoint.EndsWith("/_doc", StringComparison.OrdinalIgnoreCase)
            || endpoint.EndsWith("/_bulk", StringComparison.OrdinalIgnoreCase))
        {
            return options.Endpoint;
        }

        return $"{endpoint}/{Uri.EscapeDataString(options.Index)}/_doc";
    }

    private sealed class ElkLogger(
        string categoryName,
        ElkLoggingOptions options,
        ChannelWriter<ElkLogEvent> writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && logLevel >= options.GetMinimumLevel();
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            writer.TryWrite(new ElkLogEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Level: logLevel.ToString(),
                Category: categoryName,
                EventId: eventId.Id,
                EventName: eventId.Name,
                Message: formatter(state, exception),
                Exception: exception?.ToString(),
                Service: options.ServiceName,
                Environment: options.Environment));
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed record ElkLogEvent(
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? Exception,
        string Service,
        string Environment);
}
