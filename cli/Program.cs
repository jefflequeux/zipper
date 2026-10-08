using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;

var Owner = Environment.GetEnvironmentVariable("DOWNLOADER_GH_ORG")
    ?? throw new InvalidOperationException("Environment variable DOWNLOADER_GH_ORG is not set.");
var Pat = Environment.GetEnvironmentVariable("DOWNLOADER_PAT")
    ?? throw new InvalidOperationException("Environment variable DOWNLOADER_PAT is not set.");
const string Repo = "zipper";
const string WorkflowFile = "zipper.yml";
const string ApiBase = "https://api.github.com";

if (args.Length != 1)
{
    AnsiConsole.MarkupLine("[red]Usage:[/] downloader <url>");
    return 1;
}

var url = args[0];

using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Pat);
http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("downloader", "1.0"));
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

// ── 1. Dispatch ───────────────────────────────────────────────────────────────
AnsiConsole.MarkupLine($"[bold]Triggering workflow[/] for [cyan]{url}[/]");

var dispatchedAt = DateTime.UtcNow.AddSeconds(-5);
var escapedUrl = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
var dispatchPayload = $"{{\"ref\":\"main\",\"inputs\":{{\"filetozip\":\"{escapedUrl}\"}}}}";

var dispatchResponse = await http.PostAsync(
    $"{ApiBase}/repos/{Owner}/{Repo}/actions/workflows/{WorkflowFile}/dispatches",
    new StringContent(dispatchPayload, Encoding.UTF8, "application/json"));

if (!dispatchResponse.IsSuccessStatusCode)
{
    var body = await dispatchResponse.Content.ReadAsStringAsync();
    AnsiConsole.MarkupLine($"[red]Failed to dispatch:[/] {dispatchResponse.StatusCode}\n{body}");
    return 1;
}

// ── 2. Find the run ───────────────────────────────────────────────────────────
long runId = 0;
int runNumber = 0;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("yellow"))
    .StartAsync("Waiting for run to appear...", async ctx =>
    {
        await Task.Delay(5000);
        for (int attempt = 1; attempt <= 30 && runId == 0; attempt++)
        {
            ctx.Status($"Waiting for run to appear... [grey](attempt {attempt}/30)[/]");
            var runsJson = await http.GetStringAsync(
                $"{ApiBase}/repos/{Owner}/{Repo}/actions/workflows/{WorkflowFile}/runs?per_page=5");

            using var doc = JsonDocument.Parse(runsJson);
            foreach (var run in doc.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                if (run.GetProperty("created_at").GetDateTime().ToUniversalTime() >= dispatchedAt)
                {
                    runId     = run.GetProperty("id").GetInt64();
                    runNumber = run.GetProperty("run_number").GetInt32();
                    break;
                }
            }

            if (runId == 0) await Task.Delay(3000);
        }
    });

if (runId == 0)
{
    AnsiConsole.MarkupLine("[red]Could not find the dispatched workflow run.[/]");
    return 1;
}

AnsiConsole.MarkupLine($"[green]Found[/] run [bold]#{runNumber}[/] (ID: {runId})");

// ── 3. Wait for completion ────────────────────────────────────────────────────
string status = "";
string conclusion = "";

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("blue"))
    .StartAsync("Workflow running...", async ctx =>
    {
        for (int attempt = 1; attempt <= 60 && status != "completed"; attempt++)
        {
            var runJson = await http.GetStringAsync(
                $"{ApiBase}/repos/{Owner}/{Repo}/actions/runs/{runId}");
            using var doc = JsonDocument.Parse(runJson);
            status     = doc.RootElement.GetProperty("status").GetString() ?? "";
            conclusion = doc.RootElement.TryGetProperty("conclusion", out var c) ? (c.GetString() ?? "") : "";

            ctx.Status($"Workflow [bold]{status}[/]... [grey](poll {attempt}/60)[/]");
            if (status != "completed") await Task.Delay(10000);
        }
    });

if (status != "completed")
{
    AnsiConsole.MarkupLine("[red]Workflow did not complete in time.[/]");
    return 1;
}
if (conclusion != "success")
{
    AnsiConsole.MarkupLine($"[red]Workflow ended:[/] {conclusion}");
    return 1;
}

AnsiConsole.MarkupLine($"[green]Workflow completed[/] successfully.");

// ── 4. Find release asset ─────────────────────────────────────────────────────
var tag = $"run-{runNumber}";
AnsiConsole.MarkupLine($"Fetching release [cyan]{tag}[/]...");

var releaseJson = await http.GetStringAsync(
    $"{ApiBase}/repos/{Owner}/{Repo}/releases/tags/{tag}");

string assetUrl = "";
using (var doc = JsonDocument.Parse(releaseJson))
{
    foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
    {
        if ((asset.GetProperty("name").GetString() ?? "")
                .Equals("artifact.txt", StringComparison.OrdinalIgnoreCase))
        {
            assetUrl = asset.GetProperty("url").GetString() ?? "";
            break;
        }
    }
}

if (string.IsNullOrEmpty(assetUrl))
{
    AnsiConsole.MarkupLine("[red]Release asset 'artifact.txt' not found.[/]");
    return 1;
}

// ── 5. Download with progress bar ─────────────────────────────────────────────
const string tmpZip = "tmp.zip";

await AnsiConsole.Progress()
    .AutoRefresh(true)
    .AutoClear(false)
    .HideCompleted(false)
    .Columns(
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new TransferSpeedColumn(),
        new RemainingTimeColumn())
    .StartAsync(async ctx =>
    {
        var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        var task  = ctx.AddTask("Downloading [cyan]artifact.txt[/]", maxValue: total > 0 ? total : 100);

        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(tmpZip);

        var buffer = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read));
            task.Increment(total > 0 ? read : 0);
        }

        if (total <= 0) task.Value = task.MaxValue;
    });

// ── 6. Extract and clean up ───────────────────────────────────────────────────
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("green"))
    .StartAsync("Extracting...", async _ =>
    {
        ZipFile.ExtractToDirectory(tmpZip, ".", overwriteFiles: true);
        File.Delete(tmpZip);
        await Task.CompletedTask;
    });

// ── 7. Trigger cleanup workflow ───────────────────────────────────────────────
AnsiConsole.MarkupLine("Triggering [grey]cleanup-artifacts[/] workflow...");

var cleanupResponse = await http.PostAsync(
    $"{ApiBase}/repos/{Owner}/{Repo}/actions/workflows/cleanup-artifacts.yml/dispatches",
    new StringContent("{\"ref\":\"main\"}", Encoding.UTF8, "application/json"));

if (cleanupResponse.IsSuccessStatusCode)
    AnsiConsole.MarkupLine("[grey]Cleanup triggered.[/]");
else
    AnsiConsole.MarkupLine($"[yellow]Cleanup dispatch returned {(int)cleanupResponse.StatusCode} (non-fatal).[/]");

AnsiConsole.MarkupLine("[green bold]Done.[/]");
return 0;
