using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DyOne.Desktop.Broker;

/// <summary>
/// Smart Cell Plugin 관리(원본 런처 plugin-version.js · plugin-manager.js 이식).
/// 설치 버전: 레지스트리 Uninstall\SmartCellPlugin DisplayVersion → 없으면 SmartCell.dll 파일 버전.
/// 서버 최신 버전과 **다르면**(낮아도) 업데이트. 미설치이거나 VC++ 2015-2022 x64가 없으면 Full, 아니면 Update 설치 파일.
/// 설치: 서버에서 받기 → SHA-256 확인 → NSIS 조용히 설치(/S, 관리자 권한 요청). 이벤트 "smartcell.progress" { jobId, phase, percent }.
/// </summary>
public static partial class SmartCellModule
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SmartCellPlugin";
    private static readonly string DllPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "MOTOMAN", "MotoSimEG-VRC", "Plugins", "x64", "SmartCell.dll");

    private sealed record Asset(string FileName, string? Sha256, long Size);
    private sealed record Latest(string Version, Asset? Full, Asset? Update, string[]? SupportedMotoSim);

    public static void Register(BrokerRegistry registry, BrokerContext ctx)
    {
        // token을 주면 서버 최신 버전과 비교한 상태(state: ok | update-ready | problem | unsupported)까지 돌려준다
        registry.Register("smartcell.status", async args =>
        {
            var local = Local();
            var token = BrokerRegistry.GetString(args, "token");
            Latest? latest = null;
            string? serverError = null;
            if (token is { Length: > 0 })
                try { latest = await LatestAsync(ctx, token, CancellationToken.None); }
                catch (BrokerException ex) { serverError = ex.Message; }
            return Status(local, latest, serverError);
        });

        registry.Register("smartcell.install", async args =>
        {
            var token = BrokerRegistry.Required(args, "token");
            var jobId = BrokerRegistry.Required(args, "jobId");
            var closeMotoSim = args.TryGetProperty("closeMotoSim", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
            var latest = await LatestAsync(ctx, token, CancellationToken.None) ?? throw new BrokerException("서버에 올라간 Smart Cell Plugin 설치 파일이 없습니다.");
            var local = Local();
            var channel = local.Installed is null || !VcRedist14() ? "full" : "update";
            var asset = (channel == "full" ? latest.Full : latest.Update ?? latest.Full) ?? throw new BrokerException($"{channel} 설치 파일이 서버에 없습니다.");

            if (Process.GetProcessesByName("MotoSimEG-VRC").Length > 0 && !closeMotoSim)
                return new { status = "ask-close-motosim" };

            using var cts = ctx.StartJob(jobId);
            var exe = Path.Combine(Path.GetTempPath(), "DY ONE", $"smartcell-{channel}-{DateTime.Now:yyyyMMddHHmmss}.exe");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                await DownloadAsync(ctx, token, jobId, channel, exe, cts.Token);
                ctx.Emit("smartcell.progress", new { jobId, phase = "verifying" });
                if (asset.Sha256 is { Length: > 0 } expected)
                {
                    await using var fs = File.OpenRead(exe);
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, cts.Token));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new BrokerException("받은 설치 파일이 서버 파일과 다릅니다(SHA-256 불일치). 다시 시도해 주세요.");
                }
                if (closeMotoSim) await CloseMotoSimAsync();

                ctx.Emit("smartcell.progress", new { jobId, phase = "installing" });
                var code = await RunElevatedAsync(exe, "/S");
                var (ok, message) = InstallResult(code);
                if (!ok) throw new BrokerException(message);
                return new { status = "installed", version = latest.Version, channel, rebootRequired = code == 3010, message };
            }
            catch (OperationCanceledException)
            {
                throw new BrokerException("설치를 취소했습니다.");
            }
            finally
            {
                ctx.EndJob(jobId);
                try { File.Delete(exe); } catch (IOException) { }
            }
        });

        // 제거: 설치 프로그램이 남긴 제거 프로그램(UninstallString)을 조용히(/S) 관리자 권한으로 실행
        registry.Register("smartcell.uninstall", async _ =>
        {
            if (Process.GetProcessesByName("MotoSimEG-VRC").Length > 0) throw new BrokerException("MotoSim을 닫은 뒤 제거해 주세요.");
            string? uninstall;
            using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey)) uninstall = key?.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstall)) throw new BrokerException("제거 프로그램 정보를 찾을 수 없습니다(Windows 설정 > 앱에서 제거해 주세요).");
            var m = QuotedExe().Match(uninstall);
            var exe = m.Success ? m.Groups[1].Value : uninstall.Trim();
            if (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new BrokerException("제거 프로그램 파일이 없습니다.");
            var code = await RunElevatedAsync(exe, "/S");
            if (code == 1223) throw new BrokerException("관리자 권한 요청을 취소했습니다.");
            if (code != 0) throw new BrokerException($"제거하지 못했습니다(종료 코드 {code}).");
            return new { status = "removed" };
        });
    }

    private sealed record LocalState(string? RegistryVersion, string? DllVersion, bool DllExists, string? Installed);

    private static LocalState Local()
    {
        string? reg;
        using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey)) reg = Valid(key?.GetValue("DisplayVersion") as string);
        var exists = File.Exists(DllPath);
        var dll = exists ? Valid(FileVersionInfo.GetVersionInfo(DllPath).FileVersion) : null;
        return new LocalState(reg, dll, exists, reg ?? dll);
    }

    private static object Status(LocalState local, Latest? latest, string? serverError)
    {
        var motosim = MotoSimModule.Installs();
        string state, message;
        string? channel = null;
        if (motosim.Count == 0) (state, message) = ("unsupported", "MotoSim이 설치되어 있지 않습니다.");
        else if (latest?.SupportedMotoSim is { Length: > 0 } sup && !motosim.Any(i => sup.Contains(i.Label)))
            (state, message) = ("unsupported", "설치된 MotoSim 버전은 Smart Cell Plugin 대상이 아닙니다.");
        else if (local.RegistryVersion is not null && !local.DllExists) (state, message) = ("problem", "설치 기록은 있는데 SmartCell.dll이 없습니다. 복구(다시 설치)해 주세요.");
        else if (latest is null) (state, message) = local.Installed is null
            ? ("problem", serverError ?? "서버에 설치 파일이 없습니다.")
            : ("ok", serverError ?? "설치되어 있습니다.");
        else if (local.Installed is null)
            (state, message, channel) = latest.Full is null ? ("problem", "Full 설치 파일이 서버에 없습니다.", null) : ("update-ready", "설치할 수 있습니다.", "full");
        else if (Compare(local.Installed, latest.Version) != 0)
        {
            channel = VcRedist14() ? "update" : "full";
            var asset = channel == "full" ? latest.Full : latest.Update ?? latest.Full;
            (state, message) = asset is null ? ("problem", $"{channel} 설치 파일이 서버에 없습니다.") : ("update-ready", "새 버전이 있습니다.");
        }
        else (state, message) = ("ok", "최신 버전입니다.");

        return new
        {
            state,
            message,
            channel,
            installedVersion = local.Installed,
            registryVersion = local.RegistryVersion,
            dllVersion = local.DllVersion,
            dllPath = DllPath,
            latestVersion = latest?.Version,
            motoSimRunning = Process.GetProcessesByName("MotoSimEG-VRC").Length > 0,
            diagnosis = new[]
            {
                new { label = "MotoSim 설치", ok = motosim.Count > 0, detail = string.Join(", ", motosim.Select(i => i.Label)) },
                new { label = "설치 기록(레지스트리)", ok = local.RegistryVersion is not null, detail = local.RegistryVersion ?? "없음" },
                new { label = "SmartCell.dll", ok = local.DllExists, detail = local.DllVersion ?? DllPath },
                new { label = "VC++ 2015-2022 x64", ok = VcRedist14(), detail = VcRedist14() ? "있음" : "없음(Full로 설치)" },
            },
        };
    }

    private static async Task<Latest?> LatestAsync(BrokerContext ctx, string token, CancellationToken ct)
    {
        using var req = ctx.Request(HttpMethod.Get, "/api/motosim/plugins/smartcell/latest", token);
        using var res = await BrokerContext.Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new BrokerException(BrokerContext.ProblemMessage(body, "서버에서 최신 버전을 읽지 못했습니다."));
        return BrokerContext.Parse<Latest>(body);
    }

    private static async Task DownloadAsync(BrokerContext ctx, string token, string jobId, string channel, string dest, CancellationToken ct)
    {
        using var req = ctx.Request(HttpMethod.Get, $"/api/motosim/plugins/smartcell/download?channel={channel}", token);
        using var res = await BrokerContext.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new BrokerException(BrokerContext.ProblemMessage(await res.Content.ReadAsStringAsync(ct), "설치 파일을 받지 못했습니다."));
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 16];
        long done = 0; int n; var clock = Stopwatch.StartNew();
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (clock.ElapsedMilliseconds >= 250)
            {
                clock.Restart();
                ctx.Emit("smartcell.progress", new { jobId, phase = "downloading", percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0 });
            }
        }
        ctx.Emit("smartcell.progress", new { jobId, phase = "downloading", percent = 100 });
    }

    /// <summary>관리자 권한으로 실행하고 끝날 때까지 기다린다. UAC 취소는 1223.</summary>
    private static async Task<int> RunElevatedAsync(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe)! })
                ?? throw new BrokerException("설치 프로그램을 시작하지 못했습니다.");
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
    }

    private static (bool Ok, string Message) InstallResult(int code) => code switch
    {
        0 => (true, "설치했습니다."),
        3010 => (true, "설치했습니다. PC를 다시 시작해야 적용됩니다."),
        1223 => (false, "관리자 권한 요청을 취소했습니다."),
        2 => (false, "64비트 Windows에서만 설치할 수 있습니다."),
        3 => (false, "MotoSim이 설치되어 있지 않습니다."),
        4 => (false, "VC++ 재배포 패키지 설치에 실패했습니다."),
        5 => (false, "플러그인 폴더를 만들지 못했습니다."),
        6 => (false, "플러그인 파일을 복사하지 못했습니다."),
        7 => (false, "플러그인 등록(RegAsm)에 실패했습니다."),
        8 => (false, "MotoSim이 실행 중입니다. 닫고 다시 시도해 주세요."),
        9 => (false, "VC++ 재배포 패키지가 없습니다. Full 설치 파일이 필요합니다."),
        _ => (false, $"설치하지 못했습니다(종료 코드 {code})."),
    };

    // MotoSim 창 닫기 → 10초 기다림 → 안 닫히면 강제 종료
    private static async Task CloseMotoSimAsync()
    {
        foreach (var p in Process.GetProcessesByName("MotoSimEG-VRC")) try { p.CloseMainWindow(); } catch (InvalidOperationException) { }
        for (var i = 0; i < 20 && Process.GetProcessesByName("MotoSimEG-VRC").Length > 0; i++) await Task.Delay(500);
        foreach (var p in Process.GetProcessesByName("MotoSimEG-VRC")) try { p.Kill(); } catch (Exception) { }
    }

    private static bool VcRedist14()
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64", @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("Installed") is int v && v == 1) return true;
        }
        return false;
    }

    private static string? Valid(string? v) => v is not null && VersionText().IsMatch(v.Trim()) ? v.Trim() : null;

    internal static int Compare(string a, string b)
    {
        int[] P(string s) => s.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < 4; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")] private static partial Regex VersionText();
    [GeneratedRegex("^\"([^\"]+)\"")] private static partial Regex QuotedExe();
}
