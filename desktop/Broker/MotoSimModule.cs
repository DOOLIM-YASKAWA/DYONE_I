using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DyOne.Desktop.Broker;

/// <summary>
/// MotoSim 실행(원본 런처 main.js 367-430, 1595-1659, 2216-2240, 3677-3939 이식).
/// 설치 위치: C:\Program Files\MOTOMAN\MotoSimEG-VRC*\MotoSimEG-VRC.exe. 고른 실행 파일이 있으면 그것, 없으면 최신 연도.
/// 실행: exe "&lt;vcl&gt;" (작업 폴더 = .vcl 폴더), 자식 PATH 앞에 NT_VC*_DLL\code\bin.
/// 실행 전·종료 후에는 그 Cell 폴더를 잡고 있는 MotoSim 관련 프로세스만 정리한다.
/// 원본은 실행 전에 PC의 MODELGROUP·로봇 폴더·CONVEYOR 파일도 지웠지만, DY ONE은 PC 폴더를 건드리지 않으므로 하지 않는다(시험 후 결정).
/// 이벤트 "motosim.run" { runId, stage(cleaning|launching|running|closing|closed|failed), message, cleanupCount }.
/// </summary>
public static partial class MotoSimModule
{
    private static readonly string MotomanRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MOTOMAN");
    private const string ExeName = "MotoSimEG-VRC.exe";

    public sealed record Install(string Label, string Path, string Folder, string? DllBinPath);

    public static void Register(BrokerRegistry registry, BrokerContext ctx)
    {
        registry.Register("motosim.status", _ =>
        {
            var installs = Installs();
            return Task.FromResult<object?>(new { installed = installs.Count > 0, installs, running = Process.GetProcessesByName("MotoSimEG-VRC").Length > 0 });
        });

        // 사용자 MotoSim 실행 파일 고르기(MotoSimEG-VRC.exe만)
        registry.Register("motosim.pickExe", _ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "MotoSimEG-VRC.exe 선택",
                Filter = "MotoSim|MotoSimEG-VRC.exe",
                InitialDirectory = Directory.Exists(MotomanRoot) ? MotomanRoot : null,
            };
            if (dialog.ShowDialog(ctx.Owner) != true) return Task.FromResult<object?>(new { canceled = true });
            if (!Path.GetFileName(dialog.FileName).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                throw new BrokerException("MotoSimEG-VRC.exe 파일만 고를 수 있습니다.");
            return Task.FromResult<object?>(new { canceled = false, path = dialog.FileName, label = LabelOf(Path.GetFileName(Path.GetDirectoryName(dialog.FileName)!)) });
        });

        registry.Register("motosim.launch", args => Task.Run<object?>(() =>
        {
            var vcl = BrokerRegistry.Required(args, "vclPath");
            var runId = BrokerRegistry.GetString(args, "runId") ?? Guid.NewGuid().ToString("N");
            if (!File.Exists(vcl) || !vcl.EndsWith(".vcl", StringComparison.OrdinalIgnoreCase)) throw new BrokerException(".vcl 파일이 없습니다.");
            var install = Choose(BrokerRegistry.GetString(args, "exePath")) ?? throw new BrokerException("설치된 MotoSim을 찾을 수 없습니다.");
            var folder = Path.GetDirectoryName(Path.GetFullPath(vcl))!;

            ctx.Emit("motosim.run", new { runId, stage = "cleaning", message = "이 Cell을 잡고 있는 MotoSim 프로세스를 정리합니다." });
            var cleaned = Cleanup(folder, rootPid: 0);
            Thread.Sleep(250);

            ctx.Emit("motosim.run", new { runId, stage = "launching", message = $"MotoSim {install.Label} 실행" });
            var psi = new ProcessStartInfo(install.Path, $"\"{Path.GetFullPath(vcl)}\"") { WorkingDirectory = folder, UseShellExecute = false };
            if (install.DllBinPath is { } bin) psi.Environment["PATH"] = bin + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            var process = Process.Start(psi) ?? throw new BrokerException("MotoSim을 시작하지 못했습니다.");
            ctx.Emit("motosim.run", new { runId, stage = "running", processId = process.Id, label = install.Label });

            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();
                    ctx.Emit("motosim.run", new { runId, stage = "closing" });
                    var n = Cleanup(folder, process.Id);
                    ctx.Emit("motosim.run", new { runId, stage = "closed", cleanupCount = n });
                }
                catch (Exception ex)
                {
                    ctx.Emit("motosim.run", new { runId, stage = "failed", message = ex.Message });
                }
            });
            return new { runId, processId = process.Id, label = install.Label, exePath = install.Path, cleanupCount = cleaned };
        }));

        // MotoSim 설치 파일 받기 → 풀기 → setup.exe 실행
        registry.Register("motosim.installer", async args =>
        {
            var token = BrokerRegistry.Required(args, "token");
            var jobId = BrokerRegistry.Required(args, "jobId");
            var name = VclAnalyzer.Sanitize(Path.GetFileName(BrokerRegistry.Required(args, "name")), "motosim.zip");
            var dialog = new SaveFileDialog { Title = "MotoSim 설치 파일 저장", FileName = name, InitialDirectory = CellModule.DownloadsFolder(), Filter = "ZIP|*.zip|모든 파일|*.*" };
            if (dialog.ShowDialog(ctx.Owner) != true) return new { canceled = true };
            using var cts = ctx.StartJob(jobId);
            try
            {
                await DownloadInstallerAsync(ctx, token, jobId, name, dialog.FileName, cts.Token);
                if (!dialog.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return new { canceled = false, path = dialog.FileName, setup = (string?)null };
                var target = CellModule.UniqueFolder(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName));
                await Task.Run(() => CellModule.Extract(ctx, jobId, dialog.FileName, target, cts.Token), cts.Token);
                var setup = Directory.EnumerateFiles(target, "setup.exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
                if (setup is not null)
                    Process.Start(new ProcessStartInfo(setup) { WorkingDirectory = Path.GetDirectoryName(setup)!, UseShellExecute = true });
                return new { canceled = false, path = dialog.FileName, setup };
            }
            catch (OperationCanceledException)
            {
                throw new BrokerException("다운로드를 취소했습니다.");
            }
            finally
            {
                ctx.EndJob(jobId);
            }
        });
    }

    private static async Task DownloadInstallerAsync(BrokerContext ctx, string token, string jobId, string name, string dest, CancellationToken ct)
    {
        using var req = ctx.Request(HttpMethod.Get, $"/api/motosim/installers/download?name={Uri.EscapeDataString(name)}", token);
        using var res = await BrokerContext.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new BrokerException(BrokerContext.ProblemMessage(await res.Content.ReadAsStringAsync(ct), $"다운로드 실패({(int)res.StatusCode})"));
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0; int n;
        var clock = Stopwatch.StartNew();
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (clock.ElapsedMilliseconds >= 250)
            {
                clock.Restart();
                ctx.Emit("cell.progress", new { jobId, phase = "download", done, total, percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0 });
            }
        }
        ctx.Emit("cell.progress", new { jobId, phase = "download", done, total = done, percent = 100 });
    }

    public static List<Install> Installs()
    {
        if (!Directory.Exists(MotomanRoot)) return [];
        return Directory.EnumerateDirectories(MotomanRoot)
            .Where(d => Path.GetFileName(d).StartsWith("MotoSimEG-VRC", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, ExeName)))
            .Select(d => new Install(LabelOf(Path.GetFileName(d)), Path.Combine(d, ExeName), d, DllBin(d)))
            .OrderByDescending(i => YearPattern().Match(i.Label) is { Success: true } m ? int.Parse(m.Value) : 0)
            .ThenBy(i => i.Label, StringComparer.Create(new System.Globalization.CultureInfo("ko-KR"), false))
            .ToList();
    }

    private static Install? Choose(string? selected)
    {
        if (selected is { Length: > 0 } && File.Exists(selected) && Path.GetFileName(selected).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(selected)!;
            return Installs().FirstOrDefault(i => i.Path.Equals(selected, StringComparison.OrdinalIgnoreCase))
                ?? new Install(LabelOf(Path.GetFileName(dir)), selected, dir, DllBin(dir));
        }
        return Installs().FirstOrDefault();
    }

    private static string LabelOf(string folderName)
    {
        if (YearPattern().Match(folderName) is { Success: true } m) return m.Value;
        var rest = Regex.Replace(folderName, @"^MotoSimEG-VRC\s*", "", RegexOptions.IgnoreCase).Trim();
        return rest.Length > 0 ? rest : "기타";
    }

    private static string? DllBin(string dir)
    {
        var nt = Directory.EnumerateDirectories(dir).FirstOrDefault(d => NtDll().IsMatch(Path.GetFileName(d)));
        var bin = nt is null ? null : Path.Combine(nt, "code", "bin");
        return bin is not null && Directory.Exists(bin) ? bin : null;
    }

    /// <summary>이 폴더의 Cell을 MotoSim이 열고 있는지(업로드 전 확인)</summary>
    public static bool IsRunningIn(string folder) => Related(folder, 0).Count > 0;

    /// <summary>MotoSim 관련 프로세스 중 rootPid 자손이거나 경로·명령줄에 Cell 폴더가 들어간 것만 강제 종료. 반환값은 종료한 수.</summary>
    private static int Cleanup(string folder, int rootPid)
    {
        var n = 0;
        foreach (var pid in Related(folder, rootPid))
            try { Process.GetProcessById(pid).Kill(); n++; } catch (Exception) { /* 이미 끝났거나 권한 없음 */ }
        return n;
    }

    private sealed record ProcInfo(int ProcessId, int ParentProcessId, string? Name, string? ExecutablePath, string? CommandLine);

    internal static List<int> Related(string folder, int rootPid)
    {
        var list = Snapshot();
        var byId = list.GroupBy(p => p.ProcessId).ToDictionary(g => g.Key, g => g.First());
        var me = Environment.ProcessId;
        var result = new List<int>();
        foreach (var p in list)
        {
            if (p.ProcessId == me || p.ProcessId == 0) continue;
            var hay = $"{p.Name} {p.ExecutablePath} {p.CommandLine}";
            var name = Path.GetFileNameWithoutExtension(p.Name ?? "");
            var motoPath = MotoPath().IsMatch(hay);
            var related = Explicit().IsMatch(hay) || (ShortName().IsMatch(name) && motoPath) || (NamedProc().IsMatch(name) && motoPath);
            if (!related) continue;
            var descendant = false;
            if (rootPid > 0)
            {
                ProcInfo? cur = p;
                for (var i = 0; i < 16 && cur is not null; i++)
                {
                    if (cur.ProcessId == rootPid || cur.ParentProcessId == rootPid) { descendant = true; break; }
                    byId.TryGetValue(cur.ParentProcessId, out cur);
                }
            }
            if (descendant || hay.Contains(folder, StringComparison.OrdinalIgnoreCase)) result.Add(p.ProcessId);
        }
        return result;
    }

    // 명령줄은 WMI(Win32_Process)에서만 읽을 수 있어 PowerShell로 한 번 읽는다
    private static List<ProcInfo> Snapshot()
    {
        const string script = "$ProgressPreference = 'SilentlyContinue'; Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine | ConvertTo-Json -Compress";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var ps = Process.Start(psi)!;
        _ = ps.StandardError.ReadToEndAsync(); // 진행 메시지(CLIXML)는 버린다
        var json = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(15000);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return json.TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize<List<ProcInfo>>(json, opts) ?? []
            : [JsonSerializer.Deserialize<ProcInfo>(json, opts)!];
    }

    [GeneratedRegex(@"20\d{2}")] private static partial Regex YearPattern();
    [GeneratedRegex(@"^NT_VC\d+(_64)?_DLL$", RegexOptions.IgnoreCase)] private static partial Regex NtDll();
    [GeneratedRegex(@"\\MOTOMAN\\MotoSim|MotoSimEG-VRC|\\YRC1000|\\YAS", RegexOptions.IgnoreCase)] private static partial Regex MotoPath();
    [GeneratedRegex(@"YppMain|YRC1000|MotoSimEG-VRC|YAS VRC|YAS SV", RegexOptions.IgnoreCase)] private static partial Regex Explicit();
    [GeneratedRegex(@"^(sv|vrc)$", RegexOptions.IgnoreCase)] private static partial Regex ShortName();
    [GeneratedRegex(@"^(YppMain|YRC1000.*|MotoSim.*|MotoSimEG-VRC.*|YAS.*)$", RegexOptions.IgnoreCase)] private static partial Regex NamedProc();
}
