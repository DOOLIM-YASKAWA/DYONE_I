using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DyOne.Desktop.Broker;

/// <summary>
/// DY ONE 서버와 Cell 파일 주고받기. 업로드는 8MB 조각(서버가 정한 크기)으로 나눠 보내고, 끊기면 서버가 받은 크기부터 다시 보낸다.
/// 다운로드는 Range로 이어 받는다. 진행률은 "cell.progress" 이벤트로 웹에 보낸다(0.25초에 한 번 이하).
/// </summary>
internal static class CellTransfer
{
    private const int Retries = 5;

    public sealed record UploadStart(long ZoneId, long ProjectId, string FileName, long TotalSize, string? CommitMessage, string? MotosimVersion, string? LocalPath);
    private sealed record Session(Guid Id, long Received, int ChunkSize, string Status);
    private sealed record ChunkReply(long Received);
    public sealed record UploadResult(long CellFileId, string Path, string Sha256);

    public static async Task<UploadResult> UploadAsync(BrokerContext ctx, string token, string jobId, string zipPath, UploadStart start, CancellationToken ct)
    {
        var session = await SendJsonAsync<Session>(ctx, HttpMethod.Post, "/api/motosim/uploads", token, start, ct);
        try
        {
            await using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var total = fs.Length;
            var buffer = new byte[session.ChunkSize];
            var offset = session.Received;
            var progress = new Progress(ctx, jobId, "upload", total);
            var failures = 0;
            while (offset < total)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = offset;
                var n = await fs.ReadAtLeastAsync(buffer, (int)Math.Min(buffer.Length, total - offset), throwOnEndOfStream: true, ct);
                using var req = ctx.Request(HttpMethod.Put, $"/api/motosim/uploads/{session.Id}/chunk?offset={offset}", token);
                req.Content = new ByteArrayContent(buffer, 0, n);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                try
                {
                    using var res = await BrokerContext.Http.SendAsync(req, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    if (res.StatusCode == HttpStatusCode.Conflict)
                    {
                        // 앞 조각이 실제로는 들어갔거나 위치가 어긋났다 → 서버가 받은 크기부터 다시
                        offset = BrokerContext.Parse<ChunkReply>(body)!.Received;
                        continue;
                    }
                    if (!res.IsSuccessStatusCode) throw new BrokerException(BrokerContext.ProblemMessage(body, $"업로드 실패({(int)res.StatusCode})"));
                    offset = BrokerContext.Parse<ChunkReply>(body)!.Received;
                    failures = 0;
                    progress.Report(offset);
                }
                catch (HttpRequestException) when (++failures <= Retries)
                {
                    // 네트워크가 잠깐 끊김 → 조금 쉬었다가 서버가 받은 크기를 확인하고 이어서
                    await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
                    offset = (await SendJsonAsync<Session>(ctx, HttpMethod.Get, $"/api/motosim/uploads/{session.Id}", token, null, ct)).Received;
                }
            }
            progress.Report(total, force: true);
            ctx.Emit("cell.progress", new { jobId, phase = "finishing", percent = 100 });
            return await SendJsonAsync<UploadResult>(ctx, HttpMethod.Post, $"/api/motosim/uploads/{session.Id}/complete", token, null, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await CancelQuietlyAsync(ctx, token, session.Id);
            throw new BrokerException("업로드를 취소했습니다.");
        }
    }

    /// <summary>파일 하나를 받는다. 이미 받다 만 .part가 있으면 이어서 받는다. 반환값은 다 받은 파일 경로.</summary>
    public static async Task<string> DownloadAsync(BrokerContext ctx, string token, string jobId, long cellFileId, string destFile, CancellationToken ct)
    {
        var part = destFile + ".part";
        var failures = 0;
        while (true)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var req = ctx.Request(HttpMethod.Get, $"/api/motosim/files/{cellFileId}/download", token);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
            try
            {
                using var res = await BrokerContext.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); continue; }
                if (!res.IsSuccessStatusCode)
                    throw new BrokerException(BrokerContext.ProblemMessage(await res.Content.ReadAsStringAsync(ct), $"다운로드 실패({(int)res.StatusCode})"));
                var resumed = res.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) have = 0;
                var total = have + (res.Content.Headers.ContentLength ?? 0);
                var progress = new Progress(ctx, jobId, "download", total);
                await using (var src = await res.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    int n;
                    var done = have;
                    while ((n = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        progress.Report(done);
                    }
                }
                progress.Report(total, force: true);
                if (File.Exists(destFile)) File.Delete(destFile);
                File.Move(part, destFile);
                // 활동 기록(실패해도 다운로드는 성공으로 둔다)
                try
                {
                    using var log = ctx.Request(HttpMethod.Post, $"/api/motosim/files/{cellFileId}/downloaded", token);
                    using var _ = await BrokerContext.Http.SendAsync(log, CancellationToken.None);
                }
                catch (HttpRequestException) { }
                return destFile;
            }
            catch (HttpRequestException) when (++failures <= Retries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
            }
        }
    }

    private static async Task<T> SendJsonAsync<T>(BrokerContext ctx, HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        using var req = ctx.Request(method, path, token);
        if (body is not null) req.Content = new StringContent(BrokerContext.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await BrokerContext.Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new BrokerException("로그인이 만료되었습니다. 다시 로그인해 주세요.");
        if (!res.IsSuccessStatusCode) throw new BrokerException(BrokerContext.ProblemMessage(text, $"서버 오류({(int)res.StatusCode})"));
        return BrokerContext.Parse<T>(text) ?? throw new BrokerException("서버 응답을 읽지 못했습니다.");
    }

    private static async Task CancelQuietlyAsync(BrokerContext ctx, string token, Guid id)
    {
        try
        {
            using var req = ctx.Request(HttpMethod.Delete, $"/api/motosim/uploads/{id}", token);
            using var _ = await BrokerContext.Http.SendAsync(req, CancellationToken.None);
        }
        catch (Exception) { /* 서버 정리는 관리자 센터에서도 할 수 있다 */ }
    }

    /// <summary>진행률 이벤트(너무 자주 보내지 않게 0.25초 간격)</summary>
    private sealed class Progress(BrokerContext ctx, string jobId, string phase, long total)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastAt = -1000;
        private long _startBytes = -1;

        public void Report(long done, bool force = false)
        {
            if (_startBytes < 0) _startBytes = done;
            var now = _clock.ElapsedMilliseconds;
            if (!force && now - _lastAt < 250) return;
            _lastAt = now;
            var seconds = Math.Max(0.001, now / 1000.0);
            var speed = (done - _startBytes) / seconds;
            ctx.Emit("cell.progress", new
            {
                jobId,
                phase,
                done,
                total,
                percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0,
                bytesPerSecond = (long)speed,
                etaSeconds = speed > 1 ? (long)((total - done) / speed) : (long?)null,
            });
        }
    }
}
