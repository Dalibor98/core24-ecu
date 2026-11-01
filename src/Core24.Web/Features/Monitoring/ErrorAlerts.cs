using System.Net;
using System.Text;
using System.Threading.Channels;
using Core24.Web.Features.Notifications;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;

namespace Core24.Web.Features.Monitoring;

public class AlertOptions
{
    public const string Section = "Alerts";

    /// <summary>Who gets "something went wrong" emails (the developer). Empty: alerts are off.</summary>
    public string To { get; set; } = "";

    /// <summary>After the first error, wait this long and send everything that happened meanwhile in ONE email.</summary>
    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>After an alert, wait at least this long before the next one, so a broken server can't send hundreds.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// A Serilog "sink": Serilog hands it every log event. It keeps only errors and puts a short text
/// into a queue. It must be fast and never throw, because it runs inside every logging call.
/// </summary>
public sealed class ErrorAlertSink : ILogEventSink
{
    // Bounded: if errors arrive faster than we email them, extra ones are dropped (and counted), not stored forever.
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropWrite });
    private int dropped;

    public ChannelReader<string> Reader => queue.Reader;
    public int TakeDroppedCount() => Interlocked.Exchange(ref dropped, 0);

    public void Emit(LogEvent e)
    {
        if (e.Level < LogEventLevel.Error) return;
        // Errors about sending the alert itself would loop forever: alert fails → error → alert...
        if (e.Properties.TryGetValue("SourceContext", out var source) && source.ToString().Contains(nameof(ErrorAlertService))) return;

        var text = $"{e.Timestamp:yyyy-MM-dd HH:mm:ss} UTC{e.Timestamp:zzz} [{e.Level}] {e.RenderMessage()}";
        if (e.Exception is not null) text += Environment.NewLine + e.Exception;
        if (!queue.Writer.TryWrite(text)) Interlocked.Increment(ref dropped);
    }
}

/// <summary>Reads the queue and emails the errors in batches, at most once per cooldown.</summary>
public class ErrorAlertService(ErrorAlertSink sink, IServiceScopeFactory scopes, IOptions<AlertOptions> options, IWebHostEnvironment env, ILogger<ErrorAlertService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.To)) return;

        try
        {
            // WaitToReadAsync sleeps until the first error arrives: no polling, no CPU while all is well.
            while (await sink.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(o.BatchDelay, stoppingToken);

                var errors = new List<string>();
                while (errors.Count < 20 && sink.Reader.TryRead(out var error)) errors.Add(error);
                var more = 0;
                while (sink.Reader.TryRead(out _)) more++;
                more += sink.TakeDroppedCount();

                await SendAsync(o.To, errors, more, stoppingToken);
                await Task.Delay(o.Cooldown, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // app is shutting down
        }
    }

    private async Task SendAsync(string to, List<string> errors, int more, CancellationToken ct)
    {
        var body = new StringBuilder($"<p><b>{errors.Count + more} error(s)</b> on CORE24 ({WebUtility.HtmlEncode(env.EnvironmentName)}, {WebUtility.HtmlEncode(Environment.MachineName)}). The full log is in App_Data/logs.</p>");
        foreach (var e in errors)
            body.Append("<pre style=\"white-space:pre-wrap;font-size:12px;background:#f2f4f7;padding:8px\">").Append(WebUtility.HtmlEncode(e)).Append("</pre>");
        if (more > 0) body.Append($"<p>…and {more} more. See the log.</p>");

        try
        {
            using var scope = scopes.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            await sender.SendAsync(new EmailMessage(to, $"CORE24: {errors.Count + more} error(s)", body.ToString()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning, not Error: an Error here would queue another alert about the alert.
            log.LogWarning(ex, "Could not send the error alert email");
        }
    }
}
