using System.Net;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Validation;

/// <summary>Validação pura: não resolve DNS, abre sockets, lê arquivos ou inicia tarefas.</summary>
public static class LocalPlanValidator
{
    public static PlanValidationResult Validate(LoadTestConfiguration? configuration, Guid executionId)
    {
        var errors = new List<ConfigurationError>();
        if (configuration is null)
        {
            errors.Add(new(ConfigurationErrorCode.Required, ConfigurationField.Configuration));
            return PlanValidationResult.Rejected(errors);
        }

        if (executionId == Guid.Empty)
            errors.Add(new(ConfigurationErrorCode.Required, ConfigurationField.ExecutionId));

        ValidateRange(configuration.RequestsPerSecond, LocalEngineLimits.MaxRequestsPerSecond,
            ConfigurationField.RequestsPerSecond, errors);
        ValidateRange(configuration.DurationSeconds, LocalEngineLimits.MaxDurationSeconds,
            ConfigurationField.DurationSeconds, errors);
        ValidateRange(configuration.MaxConcurrency, LocalEngineLimits.MaxConcurrency,
            ConfigurationField.MaxConcurrency, errors);
        ValidateRange(configuration.MaxResponseBytes, LocalEngineLimits.MaxResponseBytes,
            ConfigurationField.MaxResponseBytes, errors);
        if (configuration.RequestTimeout <= TimeSpan.Zero || configuration.RequestTimeout > LocalEngineLimits.MaxRequestTimeout)
            errors.Add(new(ConfigurationErrorCode.OutOfRange, ConfigurationField.RequestTimeout));

        long plannedTotal = 0;
        try { plannedTotal = checked(configuration.RequestsPerSecond * configuration.DurationSeconds); }
        catch (OverflowException) { errors.Add(new(ConfigurationErrorCode.PlannedTotalOverflow, ConfigurationField.PlannedTotal)); }

        if (configuration.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"))
            errors.Add(new(ConfigurationErrorCode.MethodNotAllowed, ConfigurationField.Method));

        var targetError = ValidateTarget(configuration.Url, out var target, out var pinnedIpAddress);
        if (targetError.HasValue)
            errors.Add(new(targetError.Value, ConfigurationField.Url));

        ValidateHeaders(configuration.Headers, errors);
        ValidateRateLimiting(configuration, errors);
        if (!Enum.IsDefined(configuration.CaptureMode)
            || configuration.CaptureMode == RequestCaptureMode.Complete && plannedTotal > Observability.RunDiagnostics.MaximumRequests)
            errors.Add(new(ConfigurationErrorCode.OutOfRange, ConfigurationField.CaptureMode));
        if (configuration.CorrelateRequests && (configuration.CaptureMode == RequestCaptureMode.Off
            || configuration.Headers?.Any(h => h?.Name?.Equals("traceparent", StringComparison.OrdinalIgnoreCase) == true
                || h?.Name?.Equals("tracestate", StringComparison.OrdinalIgnoreCase) == true) == true))
            errors.Add(new(ConfigurationErrorCode.UnsupportedValue, ConfigurationField.CorrelateRequests));
        if (configuration.Payload is null)
            errors.Add(new(ConfigurationErrorCode.Required, ConfigurationField.Payload));
        else if (configuration.Payload.Length > LocalEngineLimits.MaxPayloadBytes)
            errors.Add(new(ConfigurationErrorCode.PayloadTooLarge, ConfigurationField.Payload));

        return errors.Count != 0
            ? PlanValidationResult.Rejected(errors)
            : PlanValidationResult.Accepted(new LoadTestPlan(executionId, target!, pinnedIpAddress!, configuration, plannedTotal));
    }

    private static void ValidateRange(long value, long maximum, ConfigurationField field, List<ConfigurationError> errors)
    {
        if (value < 1 || value > maximum)
            errors.Add(new(ConfigurationErrorCode.OutOfRange, field));
    }

    private static void ValidateRateLimiting(LoadTestConfiguration configuration, List<ConfigurationError> errors)
    {
        if (configuration.RateLimitMode is not (RateLimitMode.Normal or RateLimitMode.Authorized or RateLimitMode.Diagnostic))
            errors.Add(new(ConfigurationErrorCode.UnsupportedValue, ConfigurationField.RateLimitMode));
        if (configuration.RetryPolicy != LoadRetryPolicy.None)
            errors.Add(new(ConfigurationErrorCode.UnsupportedValue, ConfigurationField.RetryPolicy));
        if (configuration.SuccessRule != HttpSuccessRule.Completed2xxV1)
            errors.Add(new(ConfigurationErrorCode.UnsupportedValue, ConfigurationField.SuccessRule));

        // O marcador não adiciona headers nem muda a política de destino. A lista já passou
        // pela validação comum, incluindo colisões, framing, orçamento e CRLF.
        if (configuration.TestCredentialHeaderIndex is int index)
        {
            var headers = configuration.Headers;
            if (index < 0 || headers is null || headers.Length > LocalEngineLimits.MaxHeaderCount
                || index >= headers.Length || headers[index]?.Value is not string value
                || value.Length > LocalEngineLimits.MaxRequestHeaderBytes || string.IsNullOrWhiteSpace(value))
                errors.Add(new(ConfigurationErrorCode.OutOfRange, ConfigurationField.TestCredentialHeaderIndex));
        }
        else if (configuration.RateLimitMode == RateLimitMode.Authorized)
            errors.Add(new(ConfigurationErrorCode.TestCredentialRequired, ConfigurationField.TestCredentialHeaderIndex));

        if (configuration.RateLimitMode == RateLimitMode.Authorized && !configuration.TestAuthorizationDeclared)
            errors.Add(new(ConfigurationErrorCode.TestAuthorizationRequired, ConfigurationField.TestAuthorizationDeclared));
    }

    private static ConfigurationErrorCode? ValidateTarget(string? text, out Uri? target, out string? pinnedIpAddress)
    {
        target = null;
        pinnedIpAddress = null;
        if (string.IsNullOrEmpty(text))
            return ConfigurationErrorCode.Required;
        if (text.Length > LocalEngineLimits.MaxUrlCharacters || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out target))
            return ConfigurationErrorCode.InvalidUrl;
        if (target.Scheme is not ("http" or "https"))
            return ConfigurationErrorCode.SchemeNotAllowed;
        if (!string.IsNullOrEmpty(target.UserInfo))
            return ConfigurationErrorCode.UserInfoNotAllowed;
        if (target.Port < 1 || !string.IsNullOrEmpty(target.Fragment) || !target.IsWellFormedOriginalString())
            return ConfigurationErrorCode.InvalidUrl;

        // Inspecionar também a autoridade original: Uri pode normalizar formas abreviadas/numéricas de IPv4.
        int authorityStart = text.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return ConfigurationErrorCode.InvalidUrl;
        string authority = text[(authorityStart + 3)..].Split(['/', '?', '#'], 2)[0];
        if (authority.Contains('@'))
            return ConfigurationErrorCode.UserInfoNotAllowed;
        string host = authority.StartsWith('[')
            ? authority[1..authority.IndexOf(']')]
            : authority.Split(':', 2)[0];
        if (host.Contains('%') || !IPAddress.TryParse(host, out var address))
            return ConfigurationErrorCode.LoopbackLiteralRequired;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host != address.ToString())
            return ConfigurationErrorCode.LoopbackLiteralRequired;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (!IPAddress.IsLoopback(address))
            return ConfigurationErrorCode.LoopbackLiteralRequired;
        pinnedIpAddress = address.ToString();
        return null;
    }

    private static void ValidateHeaders(HttpHeader[]? headers, List<ConfigurationError> errors)
    {
        if (headers is null)
        {
            errors.Add(new(ConfigurationErrorCode.Required, ConfigurationField.Headers));
            return;
        }
        if (headers.Length > LocalEngineLimits.MaxHeaderCount)
        {
            errors.Add(new(ConfigurationErrorCode.HeadersTooLarge, ConfigurationField.Headers));
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 2; // CRLF final; cada linha usa "nome: valor\r\n".
        for (int index = 0; index < headers.Length; index++)
        {
            var header = headers[index];
            if (header?.Name is null || header.Value is null)
            {
                errors.Add(new(ConfigurationErrorCode.InvalidHeader, ConfigurationField.Headers, index));
                continue;
            }
            bytes += (long)header.Name.Length + header.Value.Length + 4;
            if (bytes > LocalEngineLimits.MaxRequestHeaderBytes)
            {
                errors.Add(new(ConfigurationErrorCode.HeadersTooLarge, ConfigurationField.Headers));
                return;
            }
            if (header.Name.Length == 0 || !header.Name.All(IsTokenCharacter)
                || header.Value.Any(c => (c < 32 && c != '\t') || c > 126))
                errors.Add(new(ConfigurationErrorCode.InvalidHeader, ConfigurationField.Headers, index));
            else if (IsTransportHeader(header.Name))
                errors.Add(new(ConfigurationErrorCode.HeaderNotAllowed, ConfigurationField.Headers, index));
            else if (!names.Add(header.Name))
                errors.Add(new(ConfigurationErrorCode.DuplicateHeader, ConfigurationField.Headers, index));
        }
    }

    private static bool IsTokenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private static bool IsTransportHeader(string name) => name.ToUpperInvariant() is
        "HOST" or "CONTENT-LENGTH" or "TRANSFER-ENCODING" or "CONNECTION" or "KEEP-ALIVE"
        or "PROXY-CONNECTION" or "TE" or "TRAILER" or "UPGRADE" or "PROXY-AUTHORIZATION"
        or "PROXY-AUTHENTICATE" or "EXPECT";
}
