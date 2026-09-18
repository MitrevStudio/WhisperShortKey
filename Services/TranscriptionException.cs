using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace whispershortkey.Services;

public enum TranscriptionFailureKind
{
    Unknown,
    Network,
    Timeout,
    RateLimited,
    ServerError,
    QuotaExhausted,
    Auth,
    BadRequest,
    ModelNotFound,
    PayloadTooLarge,
    Configuration,
    AudioUnreadable,
    MalformedResponse,
    Cancelled
}

/// <summary>
/// Every failure a transcription attempt can produce, classified once so the tray menu,
/// the balloon tip and the retry logic all describe it the same way.
/// </summary>
public sealed class TranscriptionException : Exception
{
    public TranscriptionFailureKind Kind { get; }

    /// <summary>True when repeating the exact same request could plausibly succeed.</summary>
    public bool IsTransient { get; }

    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>Provider-specific code, e.g. "insufficient_quota" or "API_KEY_INVALID".</summary>
    public string? ProviderCode { get; }

    /// <summary>Compact label for the tray menu, e.g. "Invalid API key".</summary>
    public string ShortMessage { get; }

    /// <summary>Full, human-readable explanation. Same as <see cref="Exception.Message"/>.</summary>
    public string UserMessage => Message;

    public TranscriptionException(
        TranscriptionFailureKind kind,
        string shortMessage,
        string userMessage,
        bool isTransient,
        int? statusCode = null,
        TimeSpan? retryAfter = null,
        string? providerCode = null,
        Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
        ShortMessage = shortMessage;
        IsTransient = isTransient;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ProviderCode = providerCode;
    }

    public static TranscriptionException Configuration(string message) =>
        new(TranscriptionFailureKind.Configuration, "Not configured", message, isTransient: false);

    public static TranscriptionException TooLarge(string message) =>
        new(TranscriptionFailureKind.PayloadTooLarge, "Recording too large", message, isTransient: false);

    public static TranscriptionException FromResponse(HttpResponseMessage response, string body, string provider) =>
        Classify((int)response.StatusCode, response.ReasonPhrase ?? "", body, provider, ReadRetryAfter(response));

    /// <summary>
    /// Pure classification of a failed HTTP attempt. Deliberately free of HttpResponseMessage
    /// so the whole table can be exercised directly.
    /// </summary>
    public static TranscriptionException Classify(
        int status, string reasonPhrase, string body, string provider, TimeSpan? retryAfter)
    {
        TryParseErrorBody(body, out var providerMessage, out var providerCode);

        // A proxy or captive portal answering with HTML means we never reached the API.
        if (providerMessage == null && LooksLikeHtml(body))
        {
            return new TranscriptionException(
                TranscriptionFailureKind.MalformedResponse,
                "Bad response",
                $"{provider} returned an HTML page instead of a reply ({status}). A proxy or captive portal is probably intercepting the request.",
                isTransient: true,
                status);
        }

        switch (status)
        {
            case 401:
                return Make(TranscriptionFailureKind.Auth, "Invalid API key",
                    $"Invalid API key for {provider}. Open Settings and check the key.", false);

            case 403:
                return Make(TranscriptionFailureKind.Auth, "Access denied",
                    $"{provider} refused the key. The API may not be enabled for this project, or it is blocked in your region.", false);

            case 404:
                return Make(TranscriptionFailureKind.ModelNotFound, "Model not found",
                    $"{provider} does not know this model. Pick a different one in Settings.", false);

            case 413:
                return Make(TranscriptionFailureKind.PayloadTooLarge, "Recording too large",
                    $"The recording is larger than {provider} accepts. Record in shorter bursts.", false);

            case 408:
                return Make(TranscriptionFailureKind.Timeout, "Timed out",
                    $"{provider} timed out while receiving the audio.", true);

            case 429:
            {
                if (IsQuotaExhausted(providerCode, body))
                {
                    return Make(TranscriptionFailureKind.QuotaExhausted, "Quota exhausted",
                        $"Your {provider} quota is used up. Check billing, or wait for the quota to reset.", false);
                }

                var wait = retryAfter is { } ra && ra > TimeSpan.Zero
                    ? $" Try again in about {Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds))}s."
                    : " Try again in a moment.";
                return Make(TranscriptionFailureKind.RateLimited, "Rate limited",
                    $"{provider} is rate limiting the request.{wait}", true);
            }

            case 400:
            case 422:
                // Gemini answers 400 (not 401) with API_KEY_INVALID when the key is wrong.
                if (string.Equals(providerCode, "API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    return Make(TranscriptionFailureKind.Auth, "Invalid API key",
                        $"Invalid API key for {provider}. Open Settings and check the key.", false);
                }

                return Make(TranscriptionFailureKind.BadRequest, $"{status} Bad request",
                    $"{provider} rejected the request.", false);

            default:
                if (status >= 500)
                {
                    return Make(TranscriptionFailureKind.ServerError, $"{status} Server error",
                        $"{provider} is having trouble right now ({status}). Retrying usually helps.", true);
                }

                var label = string.IsNullOrWhiteSpace(reasonPhrase) ? "Unexpected error" : $"{status} {reasonPhrase}";
                return Make(TranscriptionFailureKind.Unknown, label,
                    $"{provider} returned an unexpected response ({status}).", false);
        }

        TranscriptionException Make(TranscriptionFailureKind kind, string shortMessage, string hint, bool transient)
        {
            var message = string.IsNullOrWhiteSpace(providerMessage)
                ? hint
                : $"{hint}\n\n{provider} says: {providerMessage}";
            return new TranscriptionException(kind, shortMessage, message, transient, status, retryAfter, providerCode);
        }
    }

    /// <summary>
    /// Classifies a thrown exception. <paramref name="userToken"/> is the caller's own token:
    /// it is the only reliable way to tell "we asked it to stop" apart from "it timed out",
    /// because both surface as TaskCanceledException.
    /// </summary>
    public static TranscriptionException FromException(Exception ex, string provider, CancellationToken userToken)
    {
        switch (ex)
        {
            case TranscriptionException typed:
                return typed;

            case OperationCanceledException when userToken.IsCancellationRequested:
                return new TranscriptionException(TranscriptionFailureKind.Cancelled, "Cancelled",
                    "The transcription was cancelled.", isTransient: false, inner: ex);

            case OperationCanceledException:
                return new TranscriptionException(TranscriptionFailureKind.Timeout, "Timed out",
                    $"{provider} did not answer in time. Check your connection, then retry.",
                    isTransient: true, inner: ex);

            case HttpRequestException http:
                return FromHttpRequestException(http, provider);

            case JsonException:
                return new TranscriptionException(TranscriptionFailureKind.MalformedResponse, "Bad response",
                    $"Could not read the reply from {provider}.", isTransient: true, inner: ex);

            case FileNotFoundException:
            case DirectoryNotFoundException:
                return new TranscriptionException(TranscriptionFailureKind.AudioUnreadable, "Audio missing",
                    "The recording file is gone, so there is nothing left to transcribe.",
                    isTransient: false, inner: ex);

            case IOException:
            case UnauthorizedAccessException:
                return new TranscriptionException(TranscriptionFailureKind.AudioUnreadable, "Audio unreadable",
                    "The recording file could not be read.", isTransient: false, inner: ex);

            default:
                return new TranscriptionException(TranscriptionFailureKind.Unknown, "Unexpected error",
                    ex.Message, isTransient: false, inner: ex);
        }
    }

    private static TranscriptionException FromHttpRequestException(HttpRequestException ex, string provider)
    {
        var transient = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => true,
            HttpRequestError.ConnectionError => true,
            HttpRequestError.SecureConnectionError => true,
            HttpRequestError.ResponseEnded => true,
            HttpRequestError.ProxyTunnelError => true,
            HttpRequestError.HttpProtocolError => true,
            // Some paths leave the error Unknown; fall back to sniffing the inner exception.
            _ => ex.InnerException is SocketException or IOException
        };

        var detail = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"Could not resolve the {provider} address. Are you online?",
            HttpRequestError.ConnectionError => $"Could not connect to {provider}. Are you online?",
            HttpRequestError.SecureConnectionError => $"The secure connection to {provider} failed. A proxy or antivirus may be intercepting TLS.",
            HttpRequestError.ProxyTunnelError => $"The proxy refused the connection to {provider}.",
            _ => $"The request to {provider} failed: {ex.Message}"
        };

        return new TranscriptionException(TranscriptionFailureKind.Network, "Network error", detail, transient, inner: ex);
    }

    private static bool IsQuotaExhausted(string? providerCode, string body)
    {
        if (string.Equals(providerCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
            return true;

        // Gemini reports both per-minute limits and daily caps as RESOURCE_EXHAUSTED;
        // only the daily ones are worth telling the user to stop trying.
        return body.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
            || body.Contains("per day", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHtml(string body) => body.AsSpan().TrimStart().StartsWith("<");

    /// <summary>
    /// Reads the shape both providers share: {"error":{"message":..,"code":..,"status":..}}.
    /// Gemini puts the interesting code in error.details[].reason instead.
    /// </summary>
    private static void TryParseErrorBody(string body, out string? message, out string? code)
    {
        message = null;
        code = null;

        if (string.IsNullOrWhiteSpace(body)) return;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return;

            if (error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString();
                return;
            }

            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();

            if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                code = c.GetString();

            if (code == null && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        code = reason.GetString();
                        break;
                    }
                }
            }

            if (code == null && error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                code = s.GetString();
        }
        catch (JsonException)
        {
            // Not JSON at all - the caller decides what that means.
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null) return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
