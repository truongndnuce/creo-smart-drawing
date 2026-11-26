using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cadflow.CreoWorker;

/// <summary>The worker protocol from the worker side. The worker always calls the server.</summary>
public sealed class ServerClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public ServerClient(string baseUrl, string workerKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("X-Worker-Key", workerKey);
    }

    public async Task<WorkItem?> ClaimAsync(string[] products, string[] queues, CancellationToken ct)
    {
        var res = await _http.PostAsJsonAsync("api/worker/claim", new { products, queues }, Json, ct);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        await Ok(res);
        return await res.Content.ReadFromJsonAsync<WorkItem>(Json, ct);
    }

    public async Task ProgressAsync(string id, string? step, string? message, CancellationToken ct) =>
        await Ok(await _http.PostAsJsonAsync($"api/worker/jobs/{id}/progress", new { step, message }, Json, ct));

    public async Task UploadAsync(string id, OutputFile f, CancellationToken ct)
    {
        await using var s = File.OpenRead(f.Path);
        await Ok(await _http.PutAsync($"api/worker/jobs/{id}/artifacts/{Uri.EscapeDataString(f.Name)}?kind={f.Kind}", new StreamContent(s), ct));
    }

    public async Task CompleteAsync(string id, Dictionary<string, double> summary, CancellationToken ct) =>
        await Ok(await _http.PostAsJsonAsync($"api/worker/jobs/{id}/complete", new { summary }, Json, ct));

    public async Task FailAsync(string id, string error, bool retryable, CancellationToken ct) =>
        await Ok(await _http.PostAsJsonAsync($"api/worker/jobs/{id}/fail", new { error, retryable }, Json, ct));

    private static async Task Ok(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }
}
