using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 수담푸드(구내식당) 주간 식단을 가져온다. 하루 한 번(기본 06:30) 네이버 QR '메뉴판' 페이지를 읽는다.
/// 업체는 주 1회(보통 일요일) 이미지를 바꾸므로, 사내 AI 글자 읽기는 이미지가 바뀐 날(주 1회)에만 돈다.
///  1) 페이지 안 Next.js 데이터(__NEXT_DATA__)의 Image 갤러리 → 이미지 주소 목록(보통 주간표 1장 + 월~금 1장씩, 점심만)
///  2) 이미지가 지난번과 같으면 확인 시각만 고친다. 바뀌었으면 사내 AI(Llm:*, 이미지 입력)로 날짜별 메뉴 글자를 읽는다.
///     요일 한 장짜리 이미지 결과를 주간표보다 우선한다. 빨간 글씨(메인 메뉴)는 main=true.
/// 결과는 API와 같은 캐시 파일(FileStorage:Root\_meals\sudam.json 또는 Meals:SudamCache)에 쓴다. DB는 쓰지 않는다.
/// 사내 AI 키(Llm:ApiKey)가 없거나 실패하면 글자 없이 이미지 주소만 저장하고, 화면은 원본 이미지를 보여 준다.
/// 외부 사이트는 읽기만 한다(로그인·폼 없음).
/// </summary>
public class SudamMenuJob(IOptions<SudamMenuOptions> options, IConfiguration config, ILogger<SudamMenuJob> logger) : IWorkerJob
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SudamMenuOptions _opt = options.Value;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public string Name => "sudam-menu";
    public bool RunOnStartup => _opt.RunOnStartup;

    public TimeSpan UntilNext(DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var next = _opt.Times.Select(TimeOnly.Parse)
            .Select(t => new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, t.Hour, t.Minute, 0, localNow.Offset))
            .Select(run => run > localNow ? run : run.AddDays(1))
            .Min();
        return next - localNow;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var path = config["Meals:SudamCache"] is { Length: > 0 } p ? p
            : config["FileStorage:Root"] is { Length: > 0 } root ? Path.Combine(root, "_meals", "sudam.json")
            : throw new InvalidOperationException("FileStorage:Root 또는 Meals:SudamCache 설정이 없습니다.");

        using var pageReq = new HttpRequestMessage(HttpMethod.Get, _opt.SourceUrl);
        pageReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Mobile Safari/537.36 DYONE-Worker");
        using var pageRes = await Http.SendAsync(pageReq, ct);
        pageRes.EnsureSuccessStatusCode();
        var images = ParseImages(await pageRes.Content.ReadAsStringAsync(ct));
        if (images.Count == 0)
        {
            logger.LogWarning("식단 페이지에서 이미지를 찾지 못해 기존 캐시를 그대로 둡니다(페이지 구조 변경 가능성).");
            return;
        }

        var previous = Load(path);
        var now = DateTimeOffset.Now;
        var llmReady = !string.IsNullOrWhiteSpace(config["Llm:ApiKey"]) && !string.IsNullOrWhiteSpace(config["Llm:BaseUrl"]);
        if (previous is not null && previous.Images.SequenceEqual(images) && (previous.Extracted || !llmReady))
        {
            await SaveAsync(path, previous with { CheckedAt = now });
            logger.LogInformation("식단 이미지 변경 없음({Count}장)", images.Count);
            return;
        }

        var uploadedOn = UploadDate(images[0]);
        var days = new Dictionary<DateOnly, (SudamMenuDay Day, bool Single)>();
        string? error = null;
        if (llmReady)
        {
            foreach (var url in images)
            {
                try
                {
                    var read = await ReadImageAsync(url, ct);
                    foreach (var d in read)
                    {
                        if (ToDate(d.Date, uploadedOn) is not { } date || d.Items.Count == 0) continue;
                        var single = read.Count == 1;
                        if (!days.TryGetValue(date, out var had) || single && !had.Single)
                            days[date] = (d with { Date = date.ToString("yyyy-MM-dd"), ImageUrl = url }, single);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"사내 AI 글자 읽기 실패: {ex.Message}";
                    logger.LogWarning(ex, "식단 이미지 글자 읽기 실패 {Url}", url);
                }
            }
        }
        else error = "사내 AI 키(Llm:ApiKey)가 없어 글자를 읽지 않았습니다.";

        var cache = new SudamMenuCache(_opt.SourceUrl, now, now, uploadedOn?.ToString("yyyy-MM-dd"), images,
            days.Count > 0 && error is null, error, days.OrderBy(kv => kv.Key).Select(kv => kv.Value.Day).ToList());
        await SaveAsync(path, cache);
        logger.LogInformation("식단 갱신: 이미지 {Images}장, 날짜 {Days}일 (업로드 {Uploaded}){Error}",
            images.Count, cache.Days.Count, cache.UploadedOn, error is null ? "" : " — " + error);
    }

    /// <summary>__NEXT_DATA__ → props.pageProps.qrDetail.sessions[type=Image].attachments[].imgOriginUrl (orderId 순)</summary>
    internal static List<string> ParseImages(string html)
    {
        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) return [];
        var sessions = JsonNode.Parse(m.Groups[1].Value)?["props"]?["pageProps"]?["qrDetail"]?["sessions"]?.AsArray();
        if (sessions is null) return [];
        return sessions.Where(s => s?["type"]?.GetValue<string>() == "Image")
            .OrderBy(s => s!["orderId"]?.GetValue<int>() ?? 0)
            .SelectMany(s => (s!["attachments"]?.AsArray() ?? []).OrderBy(a => a?["orderId"]?.GetValue<int>() ?? 0))
            .Select(a => a?["imgOriginUrl"]?.GetValue<string>() ?? a?["imgWebUrl"]?.GetValue<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!).ToList();
    }

    /// <summary>이미지 주소 경로의 업로드 날짜(…/20261025_205/…).</summary>
    private static DateOnly? UploadDate(string url) =>
        Regex.Match(url, @"/(\d{8})_\d+/") is { Success: true } m && DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", out var d) ? d : null;

    /// <summary>"10/26" → 업로드 날짜 기준 연도를 붙인다(연말에 올린 1월 식단은 다음 해).</summary>
    private static DateOnly? ToDate(string text, DateOnly? uploadedOn)
    {
        var m = Regex.Match(text, @"(\d{1,2})\s*[/.월-]\s*(\d{1,2})");
        if (!m.Success) return null;
        var basis = uploadedOn ?? DateOnly.FromDateTime(DateTime.Today);
        try
        {
            var d = new DateOnly(basis.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            return d < basis.AddMonths(-6) ? d.AddYears(1) : d;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private const string Prompt = """
        구내식당 식단표 이미지다. 보이는 날짜(월/일)별로 메뉴를 위에서 아래 순서대로 적어 JSON으로만 답한다.
        - 글자를 고치거나 추측하지 말고 보이는 그대로 적는다. 날짜가 없으면 빈 배열.
        - 빨간 글씨(메인 메뉴)는 main=true, 나머지는 false.
        - 메뉴가 아닌 문구(안내문·연도·업체명)는 넣지 않는다.
        형식: {"days":[{"date":"10/26","items":[{"name":"흑미밥","main":false},{"name":"삼겹살","main":true}]}]}
        """;

    private async Task<List<SudamMenuDay>> ReadImageAsync(string url, CancellationToken ct)
    {
        // 사내 AI 서버가 외부에 못 나갈 수 있어 이미지를 받아 base64로 넘긴다.
        var bytes = await Http.GetByteArrayAsync(url, ct);
        var mime = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        using var req = new HttpRequestMessage(HttpMethod.Post, config["Llm:BaseUrl"]!.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = config["Llm:Model"] ?? "qwen3.8-27b-fp8",
            temperature = 0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Prompt },
                        new { type = "image_url", image_url = new { url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } },
                    },
                },
            },
        }), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"사내 AI 응답 오류 {(int)res.StatusCode}");
        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        if (content.LastIndexOf("</think>", StringComparison.Ordinal) is var think and >= 0) content = content[(think + 8)..];
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("사내 AI가 JSON을 돌려주지 않았습니다.");
        var j = JsonNode.Parse(content[start..(end + 1)]);
        return (j?["days"]?.AsArray() ?? []).Select(d => new SudamMenuDay(
                d?["date"]?.ToString() ?? "",
                (d?["items"]?.AsArray() ?? [])
                    .Select(i => new SudamMenuItem((i?["name"]?.ToString() ?? "").Trim(), i?["main"]?.GetValueKind() == JsonValueKind.True))
                    .Where(i => i.Name.Length > 0).ToList(),
                null))
            .ToList();
    }

    private static SudamMenuCache? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SudamMenuCache>(File.ReadAllText(path), Json) : null; }
        catch (JsonException) { return null; }
    }

    private static async Task SaveAsync(string path, SudamMenuCache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(cache, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>캐시 파일 모양. API(/api/meals/sudam)는 이 파일을 그대로 돌려준다.</summary>
public sealed record SudamMenuCache(
    string SourceUrl, DateTimeOffset CheckedAt, DateTimeOffset UpdatedAt, string? UploadedOn,
    List<string> Images, bool Extracted, string? ExtractError, List<SudamMenuDay> Days);

/// <summary>Date는 yyyy-MM-dd, ImageUrl은 그 날 메뉴를 읽은 이미지.</summary>
public sealed record SudamMenuDay(string Date, List<SudamMenuItem> Items, string? ImageUrl);

public sealed record SudamMenuItem(string Name, bool Main);
