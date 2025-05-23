﻿using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using System.Diagnostics;

namespace Serilog {
public static class HighlightSerilogExtensions {
    public static LoggerConfiguration HighlightOpenTelemetry(this LoggerSinkConfiguration loggerConfiguration,
        Action<Highlight.OpenTelemetry.Config> configure)
    {
        Highlight.OpenTelemetry.Config config = new()
        {
            ProjectId = "",
            ServiceName = ""
        };
        configure(config);
        return loggerConfiguration.OpenTelemetry(options => {
            options.Protocol = Highlight.OpenTelemetry.Protocol;
            options.Endpoint = config.OtlpEndpoint.ToString();
            options.ResourceAttributes = Highlight.OpenTelemetry.GetResourceAttributes();
        });
    }

    public static LoggerConfiguration WithHighlight(this LoggerEnrichmentConfiguration loggerConfiguration)
    {
        return loggerConfiguration.With(new Highlight.LogEnricher());
    }
}
}

namespace Microsoft.Extensions.DependencyInjection {
public static class HighlightCollectionExtensions {
    public static IServiceCollection AddHighlightInstrumentation(this IServiceCollection services,
        Action<Highlight.OpenTelemetry.Config> configure)
    {
        if (configure == null)
            throw new ArgumentNullException(nameof(configure));

        Highlight.OpenTelemetry.InstrumentServices(services, configure);
        return services;
    }

    public static ILoggingBuilder AddHighlightInstrumentation(this ILoggingBuilder logging,
        Action<Highlight.OpenTelemetry.Config> configure)
    {
        if (configure == null)
            throw new ArgumentNullException(nameof(configure));

        Highlight.OpenTelemetry.InstrumentLogging(logging, configure);
        return logging;
    }
}
}


namespace Highlight {

public class TraceProcessor : BaseProcessor<Activity> {
    public override void OnStart(Activity data)
    {
        var ctx = OpenTelemetry.GetHighlightContext();
        foreach (var entry in ctx)
        {
            data.SetTag(entry.Key, entry.Value);
        }

        base.OnStart(data);
    }
}

public class LogProcessor : BaseProcessor<LogRecord> {
    public override void OnStart(LogRecord data)
    {
        var ctx = OpenTelemetry.GetHighlightContext();
        var attributes = ctx.Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)).ToList();
        if (data.Attributes != null)
        {
            attributes = attributes.Concat(data.Attributes).ToList();
        }

        data.Attributes = attributes;
        base.OnStart(data);
    }
}

public class LogEnricher : ILogEventEnricher {
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory pf)
    {
        var ctx = OpenTelemetry.GetHighlightContext();
        foreach (var entry in ctx)
        {
            logEvent.AddOrUpdateProperty(pf.CreateProperty(entry.Key, entry.Value));
        }
    }
}


public static class OpenTelemetry {
    public const OtlpProtocol Protocol = OtlpProtocol.Grpc;
    public const OtlpExportProtocol ExportProtocol = OtlpExportProtocol.Grpc;
    public const string HighlightHeader = "x-highlight-request";

    public class Config {
        public string ProjectId = "";
        public string ServiceName = "";
        public Uri OtlpEndpoint = new("https://otel.highlight.io:4317");

        public Dictionary<string, object> ResourceAttributes = [];
    };

    public static Dictionary<string, object> GetResourceAttributes()
    {
        return _config.ResourceAttributes.Concat(new Dictionary<string, object>
        {
            ["highlight.project_id"] = _config.ProjectId,
            ["service.name"] = _config.ServiceName,
            ["telemetry.distro.name"] = "Highlight.ASPCore",
            ["telemetry.distro.version"] = "0.2.16",
        }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    static Config _config = new()
    {
        ProjectId = "",
        ServiceName = "default-service-name"
    };

    static readonly Random Random = new();

    public static Dictionary<string, string?> GetHighlightContext()
    {
        var ctx = new Dictionary<string, string?>
        {
            {
                "highlight.project_id", _config.ProjectId
            },
            {
                "service.name", _config.ServiceName
            },
        };

        var headerValue = Baggage.Current.GetBaggage(HighlightHeader);
        if (headerValue == null) return ctx;

        string?[] parts = headerValue.Split("/");
        if (parts.Length < 2) return ctx;

        ctx["highlight.session_id"] = parts[0];
        // rely on `traceparent` w3c parent context propagation instead of highlight.trace_id
        return ctx;
    }

    public static Config GetConfig()
    {
        return _config;
    }

    private static void EnrichWithHttpRequest(Activity activity, HttpRequest httpRequest)
    {
        activity.SetTag("http.client_ip", httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString());
        activity.SetTag("http.flavor", httpRequest.HttpContext.Request.Protocol);
        activity.SetTag("http.host", httpRequest.Host);
        activity.SetTag("http.method", httpRequest.Method);
        activity.SetTag("http.request_content_length", httpRequest.ContentLength);
        activity.SetTag("http.route", httpRequest.RouteValues["action"]);
        activity.SetTag("http.scheme", httpRequest.Scheme);
        activity.SetTag("http.server_name", httpRequest.HttpContext.Request.Host.Host);
        activity.SetTag("http.url", httpRequest.Path);
        activity.SetTag("http.user_agent", httpRequest.Headers["User-Agent"]);

        for (var i = 0; i < httpRequest.Headers.Count; i++)
        {
            var header = httpRequest.Headers.ElementAt(i);
            activity.SetTag($"http.request.header.{header.Key}", header.Value);
        }

        var (sessionId, requestId) = ExtractContext(httpRequest);
        activity.SetTag("highlight.session_id", sessionId);
        // rely on `traceparent` w3c parent context propagation instead of highlight.trace_id
        Baggage.Current.SetBaggage(new KeyValuePair<string, string?>(HighlightHeader, $"{sessionId}/{requestId}"));
    }

    private static void EnrichWithHttpResponse(Activity activity, HttpResponse httpResponse)
    {
        activity.SetTag("http.status_code", httpResponse.StatusCode);
        activity.SetTag("http.response_content_length", httpResponse.ContentLength);

        for (var i = 0; i < httpResponse.Headers.Count; i++)
        {
            var header = httpResponse.Headers.ElementAt(i);
            activity.SetTag($"http.response.header.{header.Key}", header.Value);
        }
    }

    private static (string, string) ExtractContext(HttpRequest httpRequest)
    {
        var headerValues = httpRequest.Headers[HighlightHeader];
        if (headerValues is [not null, ..])
        {
            var parts = headerValues[0]?.Split("/");
            if (parts?.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        var sessionId = httpRequest.Cookies["sessionID"] ?? new string(Enumerable
            .Repeat("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 28)
            .Select(s => s[Random.Next(s.Length)]).ToArray());

        var sessionDataKey = $"sessionData_{sessionId}";
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessionData = httpRequest.Cookies[sessionDataKey] ??
                          $"{{\"sessionSecureID\":\"{sessionId}\",\"projectID\":\"{_config.ProjectId}\",\"payloadID\":1,\"sessionStartTime\":{start},\"lastPushTime\":{start}}}";

        var opts = new CookieOptions
        {
            Expires = DateTimeOffset.Now.AddMinutes(15)
        };
        httpRequest.HttpContext.Response.Cookies.Append("sessionID", sessionId, opts);
        httpRequest.HttpContext.Response.Cookies.Append(sessionDataKey, sessionData, opts);
        return (sessionId, "");
    }

    public static void InstrumentServices(IServiceCollection services, Action<Config> configure)
    {
        configure(_config);
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddAttributes(GetResourceAttributes()).AddService(_config.ServiceName))
                .AddHttpClientInstrumentation()
                .AddGrpcClientInstrumentation()
                .AddSqlClientInstrumentation(option => { option.SetDbStatementForText = true; })
                .AddQuartzInstrumentation()
                .AddWcfInstrumentation()
                .AddAspNetCoreInstrumentation(options => {
                    options.RecordException = true;
                    options.EnrichWithHttpRequest = EnrichWithHttpRequest;
                    options.EnrichWithHttpResponse = EnrichWithHttpResponse;
                })
                .AddSource([_config.ServiceName, "Microsoft.Azure.Functions.Worker"])
                .AddProcessor(new TraceProcessor())
                .AddOtlpExporter(options => {
                    options.Endpoint = _config.OtlpEndpoint;
                    options.Protocol = ExportProtocol;
                    options.BatchExportProcessorOptions.MaxExportBatchSize = 10000;
                    options.BatchExportProcessorOptions.MaxQueueSize = 10000;
                    options.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 1000;
                }))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddAttributes(GetResourceAttributes()).AddService(_config.ServiceName))
                .AddMeter(_config.ServiceName)
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddAspNetCoreInstrumentation()
                .AddReader(new PeriodicExportingMetricReader(new OtlpMetricExporter(new OtlpExporterOptions
                {
                    Endpoint = _config.OtlpEndpoint,
                    Protocol = ExportProtocol
                }))));
    }

    public static void InstrumentLogging(ILoggingBuilder logging, Action<Config> configure)
    {
        configure(_config);
        logging.AddOpenTelemetry(options => {
            options
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddAttributes(GetResourceAttributes()))
                .AddProcessor(new LogProcessor())
                .AddOtlpExporter(exporterOptions => {
                    exporterOptions.Endpoint = _config.OtlpEndpoint;
                    exporterOptions.Protocol = ExportProtocol;
                    exporterOptions.BatchExportProcessorOptions.MaxExportBatchSize = 10000;
                    exporterOptions.BatchExportProcessorOptions.MaxQueueSize = 10000;
                    exporterOptions.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 1000;
                });
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
        });
    }
}
}
