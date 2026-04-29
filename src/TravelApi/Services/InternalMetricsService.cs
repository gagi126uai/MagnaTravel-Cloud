using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Hangfire.Common;
using Hangfire.Server;

namespace TravelApi.Services;

public sealed class InternalMetricsService
{
    private static readonly double[] DurationBuckets = { 0.05, 0.1, 0.3, 0.8, 1.5, 3, 10 };
    private static readonly double[] JobDurationBuckets = { 0.5, 1, 5, 15, 30, 60, 300 };
    private readonly ConcurrentDictionary<RequestMetricKey, RequestMetric> _requests = new();
    private readonly ConcurrentDictionary<JobMetricKey, RequestMetric> _jobs = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _activeRequests;
    private long _databaseReady;

    public void IncrementActiveRequests() => Interlocked.Increment(ref _activeRequests);

    public void DecrementActiveRequests() => Interlocked.Decrement(ref _activeRequests);

    public void RecordRequest(string method, string route, int statusCode, double durationSeconds)
    {
        var key = new RequestMetricKey(
            method.ToUpperInvariant(),
            string.IsNullOrWhiteSpace(route) ? "unknown" : route,
            statusCode.ToString());

        var metric = _requests.GetOrAdd(key, _ => new RequestMetric(DurationBuckets.Length));
        metric.Record(durationSeconds, DurationBuckets);
    }

    public void RecordJob(string jobType, string method, string status, double durationSeconds)
    {
        var key = new JobMetricKey(
            string.IsNullOrWhiteSpace(jobType) ? "unknown" : jobType,
            string.IsNullOrWhiteSpace(method) ? "unknown" : method,
            string.IsNullOrWhiteSpace(status) ? "unknown" : status);

        var metric = _jobs.GetOrAdd(key, _ => new RequestMetric(JobDurationBuckets.Length));
        metric.Record(durationSeconds, JobDurationBuckets);
    }

    public void SetDatabaseReady(bool ready) => Interlocked.Exchange(ref _databaseReady, ready ? 1 : 0);

    public string RenderPrometheus()
    {
        var output = new StringBuilder();
        output.AppendLine("# HELP magnatravel_process_uptime_seconds Process uptime in seconds.");
        output.AppendLine("# TYPE magnatravel_process_uptime_seconds gauge");
        output.AppendLine($"magnatravel_process_uptime_seconds {(DateTimeOffset.UtcNow - _startedAt).TotalSeconds:F0}");
        output.AppendLine();

        output.AppendLine("# HELP magnatravel_http_requests_active Current active HTTP requests.");
        output.AppendLine("# TYPE magnatravel_http_requests_active gauge");
        output.AppendLine($"magnatravel_http_requests_active {Interlocked.Read(ref _activeRequests)}");
        output.AppendLine();

        output.AppendLine("# HELP magnatravel_database_ready Database readiness as observed by /health/ready.");
        output.AppendLine("# TYPE magnatravel_database_ready gauge");
        output.AppendLine($"magnatravel_database_ready {Interlocked.Read(ref _databaseReady)}");
        output.AppendLine();

        output.AppendLine("# HELP magnatravel_http_requests_total Total HTTP requests by method, route and status.");
        output.AppendLine("# TYPE magnatravel_http_requests_total counter");
        output.AppendLine("# HELP magnatravel_http_request_duration_seconds HTTP request duration histogram.");
        output.AppendLine("# TYPE magnatravel_http_request_duration_seconds histogram");

        foreach (var item in _requests.OrderBy(item => item.Key.Route).ThenBy(item => item.Key.Method).ThenBy(item => item.Key.StatusCode))
        {
            var snapshot = item.Value.Snapshot();
            var labels = $"method=\"{EscapeLabel(item.Key.Method)}\",route=\"{EscapeLabel(item.Key.Route)}\",status=\"{EscapeLabel(item.Key.StatusCode)}\"";
            output.AppendLine($"magnatravel_http_requests_total{{{labels}}} {snapshot.Count}");

            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                output.AppendLine($"magnatravel_http_request_duration_seconds_bucket{{{labels},le=\"{DurationBuckets[i]:0.###}\"}} {cumulative}");
            }

            output.AppendLine($"magnatravel_http_request_duration_seconds_bucket{{{labels},le=\"+Inf\"}} {snapshot.Count}");
            output.AppendLine($"magnatravel_http_request_duration_seconds_sum{{{labels}}} {snapshot.SumSeconds:0.######}");
            output.AppendLine($"magnatravel_http_request_duration_seconds_count{{{labels}}} {snapshot.Count}");
        }

        output.AppendLine();
        output.AppendLine("# HELP magnatravel_hangfire_jobs_total Total Hangfire jobs by type, method and status.");
        output.AppendLine("# TYPE magnatravel_hangfire_jobs_total counter");
        output.AppendLine("# HELP magnatravel_hangfire_job_duration_seconds Hangfire job duration histogram.");
        output.AppendLine("# TYPE magnatravel_hangfire_job_duration_seconds histogram");

        foreach (var item in _jobs.OrderBy(item => item.Key.JobType).ThenBy(item => item.Key.Method).ThenBy(item => item.Key.Status))
        {
            var snapshot = item.Value.Snapshot();
            var labels = $"job_type=\"{EscapeLabel(item.Key.JobType)}\",method=\"{EscapeLabel(item.Key.Method)}\",status=\"{EscapeLabel(item.Key.Status)}\"";
            output.AppendLine($"magnatravel_hangfire_jobs_total{{{labels}}} {snapshot.Count}");

            long cumulative = 0;
            for (var i = 0; i < JobDurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                output.AppendLine($"magnatravel_hangfire_job_duration_seconds_bucket{{{labels},le=\"{JobDurationBuckets[i]:0.###}\"}} {cumulative}");
            }

            output.AppendLine($"magnatravel_hangfire_job_duration_seconds_bucket{{{labels},le=\"+Inf\"}} {snapshot.Count}");
            output.AppendLine($"magnatravel_hangfire_job_duration_seconds_sum{{{labels}}} {snapshot.SumSeconds:0.######}");
            output.AppendLine($"magnatravel_hangfire_job_duration_seconds_count{{{labels}}} {snapshot.Count}");
        }

        return output.ToString();
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private readonly record struct RequestMetricKey(string Method, string Route, string StatusCode);
    private readonly record struct JobMetricKey(string JobType, string Method, string Status);

    private sealed class RequestMetric
    {
        private readonly object _gate = new();
        private readonly long[] _buckets;
        private long _count;
        private double _sumSeconds;

        public RequestMetric(int bucketCount)
        {
            _buckets = new long[bucketCount];
        }

        public void Record(double durationSeconds, IReadOnlyList<double> durationBuckets)
        {
            lock (_gate)
            {
                _count++;
                _sumSeconds += durationSeconds;

                for (var i = 0; i < durationBuckets.Count; i++)
                {
                    if (durationSeconds <= durationBuckets[i])
                    {
                        _buckets[i]++;
                        break;
                    }
                }
            }
        }

        public RequestMetricSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new RequestMetricSnapshot(_count, _sumSeconds, _buckets.ToArray());
            }
        }
    }

    private sealed record RequestMetricSnapshot(long Count, double SumSeconds, long[] Buckets);
}

public sealed class HangfireMetricsFilter : JobFilterAttribute, IServerFilter
{
    private const string StartedAtKey = "magnatravel_metrics_started_at";
    private readonly InternalMetricsService _metrics;

    public HangfireMetricsFilter(InternalMetricsService metrics)
    {
        _metrics = metrics;
    }

    public void OnPerforming(PerformingContext filterContext)
    {
        filterContext.Items[StartedAtKey] = Stopwatch.GetTimestamp();
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        var elapsedSeconds = 0d;
        if (filterContext.Items.TryGetValue(StartedAtKey, out var startedAtValue) && startedAtValue is long startedAt)
        {
            elapsedSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        }

        var job = filterContext.BackgroundJob?.Job;
        var jobType = job?.Type.Name ?? "unknown";
        var method = job?.Method.Name ?? "unknown";
        var status = filterContext.Exception is null ? "succeeded" : "failed";
        _metrics.RecordJob(jobType, method, status, elapsedSeconds);
    }
}

public sealed class InternalMetricsMiddleware
{
    private readonly RequestDelegate _next;

    public InternalMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, InternalMetricsService metrics)
    {
        if (context.Request.Path.StartsWithSegments("/internal/metrics"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        metrics.IncrementActiveRequests();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            metrics.DecrementActiveRequests();
            var route = context.GetEndpoint() is RouteEndpoint routeEndpoint
                ? routeEndpoint.RoutePattern.RawText ?? "unknown"
                : "unmatched";
            metrics.RecordRequest(context.Request.Method, route, context.Response.StatusCode, stopwatch.Elapsed.TotalSeconds);
        }
    }
}
