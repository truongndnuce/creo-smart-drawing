using System.Text.Json;
using Cadflow.Domain.Jobs;

namespace Cadflow.Application.Jobs;

public sealed record DayStats(string Date, int Total, int Succeeded, int Failed, double? AvgBuildSeconds);

public sealed record StatsResult(int Days, int Total, IReadOnlyDictionary<string, int> ByStatus, double? SuccessRate, double? AvgBuildSeconds, IReadOnlyList<DayStats> ByDay);

/// <summary>Pure computation of the Dashboard numbers from a list of jobs (server local time for the day buckets).</summary>
public static class JobStats
{
    /// <summary>Build time = from when the worker took the job until it finished, successful jobs only.</summary>
    public static double? BuildSeconds(Job j) =>
        j.Status == JobStatus.Succeeded && j.StartedAt is { } s && j.FinishedAt is { } f && f >= s ? (f - s).TotalSeconds : null;

    private static double? Avg(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 1);
    }

    public static StatsResult Compute(IEnumerable<Job> jobs, int days, DateTimeOffset now)
    {
        var span = Math.Clamp(days, 1, 90);
        var firstDay = now.ToLocalTime().Date.AddDays(-(span - 1));
        var list = jobs.Where(j => j.CreatedAt.ToLocalTime().Date >= firstDay).ToList();

        var byStatus = Enum.GetValues<JobStatus>().ToDictionary(s => JsonNamingPolicy.CamelCase.ConvertName(s.ToString()), s => list.Count(j => j.Status == s));
        int ok = byStatus["succeeded"], bad = byStatus["failed"];

        var byDay = Enumerable.Range(0, span).Select(i =>
        {
            var day = firstDay.AddDays(i);
            var dayJobs = list.Where(j => j.CreatedAt.ToLocalTime().Date == day).ToList();
            return new DayStats(day.ToString("yyyy-MM-dd"), dayJobs.Count, dayJobs.Count(j => j.Status == JobStatus.Succeeded),
                dayJobs.Count(j => j.Status == JobStatus.Failed), Avg(dayJobs.Select(BuildSeconds)));
        }).ToList();

        return new StatsResult(span, list.Count, byStatus, ok + bad == 0 ? null : Math.Round(100.0 * ok / (ok + bad), 1), Avg(list.Select(BuildSeconds)), byDay);
    }
}
