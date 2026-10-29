using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DyOne.Desktop.Broker;

/// <summary>
/// Cell 파일 관리(원본 MotoSim Launcher 이식).
/// 원칙(2026-10-21 결정): PC의 Cell 폴더는 읽기만 한다. 서버에 필요 없는 파일(백업·안 쓰는 로봇 폴더)은 zip에서만 빼고,
/// 안 쓰는 MODELGROUP을 지운 .vcl은 zip 안에만 넣는다. 내려받을 때도 기존 폴더를 지우지 않고 " (2)" 폴더에 푼다.
/// 진행률은 "cell.progress" 이벤트 { jobId, phase(compressing|upload|finishing|download|extracting), done, total, percent }.
/// </summary>
public static class CellModule
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "DY ONE", "motosim");

    public static void Register(BrokerRegistry registry, BrokerContext ctx)
    {
        // .vcl 고르기 → 업로드 계획(빠질 파일·폴더, 크기, MotoSim 버전)
        registry.Register("cell.pickVcl", _ =>
        {
            var dialog = new OpenFileDialog { Title = "Cell의 .vcl 파일 선택", Filter = "VCL Files|*.vcl", CheckFileExists = true };
            if (dialog.ShowDialog(ctx.Owner) != true) return Task.FromResult<object?>(new { canceled = true });
            return Task.Run<object?>(() => Describe(VclAnalyzer.Analyze(dialog.FileName)));
        });

        // 이미 고른 .vcl 다시 분석(웹이 기억해 둔 경로)
        registry.Register("cell.inspect", args => Task.Run<object?>(() => Describe(VclAnalyzer.Analyze(BrokerRegistry.Required(args, "vclPath")))));

        // zip 만들기 → 나눠 올리기
        registry.Register("cell.upload", async args =>
        {
            var token = BrokerRegistry.Required(args, "token");
            var jobId = BrokerRegistry.Required(args, "jobId");
            var vclPath = BrokerRegistry.Required(args, "vclPath");
            var zoneId = BrokerRegistry.GetLong(args, "zoneId") ?? throw new BrokerException("Zone을 선택해 주세요.");
            var projectId = BrokerRegistry.GetLong(args, "projectId") ?? throw new BrokerException("프로젝트를 선택해 주세요.");
            if (MotoSimModule.IsRunningIn(Path.GetDirectoryName(vclPath)!))
                throw new BrokerException("이 Cell을 MotoSim에서 열고 있습니다. MotoSim을 닫은 뒤 업로드해 주세요.");

            using var cts = ctx.StartJob(jobId);
            var zip = Path.Combine(TempRoot, "uploads", $"{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");
            try
            {
                var plan = await Task.Run(() => VclAnalyzer.Analyze(vclPath), cts.Token);
                var name = BrokerRegistry.GetString(args, "fileName") is { Length: > 0 } n ? n : $"{VclAnalyzer.Sanitize(Path.GetFileName(plan.CellFolder), "cell")}.zip";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
                await Task.Run(() => BuildZip(ctx, jobId, plan, zip, cts.Token), cts.Token);
                var result = await CellTransfer.UploadAsync(ctx, token, jobId, zip,
                    new CellTransfer.UploadStart(zoneId, projectId, name, new FileInfo(zip).Length,
                        BrokerRegistry.GetString(args, "commitMessage"), plan.MotosimVersion, plan.VclPath), cts.Token);
                return new { result.CellFileId, fileName = name, removedModelGroups = plan.RemovedModelGroups, excludedFolders = plan.ExcludedFolders, excludedBackupCount = plan.ExcludedBackupFiles.Count };
            }
            catch (OperationCanceledException)
            {
                throw new BrokerException("업로드를 취소했습니다.");
            }
            finally
            {
                ctx.EndJob(jobId);
                try { File.Delete(zip); } catch (IOException) { }
            }
        });

        // 내려받기 → 풀기. 기본 위치는 내 PC 다운로드 폴더, folder=pick 이면 폴더를 고른다. 같은 이름 폴더가 있으면 " (2)"…
        registry.Register("cell.download", async args =>
        {
            var token = BrokerRegistry.Required(args, "token");
            var jobId = BrokerRegistry.Required(args, "jobId");
            var cellFileId = BrokerRegistry.GetLong(args, "cellFileId") ?? throw new BrokerException("파일을 선택해 주세요.");
            var fileName = VclAnalyzer.Sanitize(Path.GetFileName(BrokerRegistry.GetString(args, "fileName") ?? ""), "cell.zip");
            var cellName = BrokerRegistry.GetString(args, "cellName");
            var root = DownloadsFolder();
            if (BrokerRegistry.GetString(args, "folder") == "pick")
            {
                var dialog = new OpenFolderDialog { Title = "Cell을 받을 폴더 선택", InitialDirectory = root };
                if (dialog.ShowDialog(ctx.Owner) != true) return new { canceled = true };
                root = dialog.FolderName;
            }

            using var cts = ctx.StartJob(jobId);
            Directory.CreateDirectory(Path.Combine(TempRoot, "downloads"));
            var zip = Path.Combine(TempRoot, "downloads", $"{cellFileId}-{fileName}");
            string? target = null;
            try
            {
                await CellTransfer.DownloadAsync(ctx, token, jobId, cellFileId, zip, cts.Token);
                target = UniqueFolder(root, VclAnalyzer.Sanitize(Path.GetFileNameWithoutExtension(fileName), "cell"));
                await Task.Run(() => Extract(ctx, jobId, zip, target, cts.Token), cts.Token);
                File.Delete(zip);
                var vcl = VclAnalyzer.FindBestVcl(target, cellName);
                return new { canceled = false, folder = target, vclPath = vcl };
            }
            catch (OperationCanceledException)
            {
                // 받다 만 .part는 남겨 다음에 이어 받는다. 풀다 만 폴더는 지운다(방금 우리가 만든 폴더만).
                if (target is not null) try { Directory.Delete(target, true); } catch (IOException) { }
                throw new BrokerException("다운로드를 취소했습니다.");
            }
            finally
            {
                ctx.EndJob(jobId);
            }
        });

        // 탐색기로 폴더 열기(있는 폴더만)
        registry.Register("cell.openFolder", args =>
        {
            var path = BrokerRegistry.Required(args, "path");
            var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            if (!Directory.Exists(dir)) throw new BrokerException("폴더가 없습니다.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetFullPath(dir)}\"") { UseShellExecute = true });
            return Task.FromResult<object?>(new { opened = true });
        });

        // 웹이 기억한 마지막 작업 .vcl이 아직 PC에 있는지
        registry.Register("cell.exists", args =>
        {
            var path = BrokerRegistry.GetString(args, "vclPath");
            return Task.FromResult<object?>(new { exists = path is { Length: > 0 } && File.Exists(path) && path.EndsWith(".vcl", StringComparison.OrdinalIgnoreCase) });
        });
    }

    private static object Describe(VclAnalyzer.Plan p) => new
    {
        canceled = false,
        vclPath = p.VclPath,
        folder = p.CellFolder,
        folderName = Path.GetFileName(p.CellFolder),
        motosimVersion = p.MotosimVersion,
        fileCount = p.FileCount,
        totalBytes = p.TotalBytes,
        includedFileCount = p.IncludedFileCount,
        includedBytes = p.IncludedBytes,
        removedModelGroups = p.RemovedModelGroups,
        excludedFolders = p.ExcludedFolders,
        excludedBackupFiles = p.ExcludedBackupFiles.Take(50).ToArray(),
        excludedBackupCount = p.ExcludedBackupFiles.Count,
    };

    /// <summary>Cell 폴더 내용을 zip 루트에 담는다(원본 7z `a -tzip out * -r` 과 같은 배치). 빠지는 것: 백업 파일, 안 쓰는 로봇 폴더.</summary>
    internal static void BuildZip(BrokerContext? ctx, string jobId, VclAnalyzer.Plan plan, string zipPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var excluded = plan.ExcludedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(plan.CellFolder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(plan.CellFolder, f)))
            .Where(x => !VclAnalyzer.IsBackupArtifact(Path.GetFileName(x.Full)))
            .Where(x => !(x.Rel.Contains(Path.DirectorySeparatorChar) && excluded.Contains(x.Rel.Split(Path.DirectorySeparatorChar)[0])))
            .ToList();
        long total = files.Sum(x => new FileInfo(x.Full).Length), done = 0;
        var lastEmit = Stopwatch.StartNew();
        var buffer = new byte[1 << 16];
        using var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        foreach (var (full, rel) in files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(full);
            using var dst = entry.Open();
            if (plan.VclChanged && string.Equals(Path.GetFullPath(full), plan.VclPath, StringComparison.OrdinalIgnoreCase))
            {
                // 안 쓰는 MODELGROUP을 지운 .vcl(원본과 같은 latin1 바이트) — zip 안에만 들어간다
                dst.Write(VclAnalyzer.Latin1.GetBytes(plan.CleanedVclText));
                done += new FileInfo(full).Length;
                continue;
            }
            // MotoSim이 열어 둔 파일도 읽을 수 있게 공유 모드를 넓게
            using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buffer, 0, n);
                done += n;
                if (ctx is not null && lastEmit.ElapsedMilliseconds >= 250)
                {
                    lastEmit.Restart();
                    ctx.Emit("cell.progress", new { jobId, phase = "compressing", done, total, percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0 });
                }
            }
        }
        ctx?.Emit("cell.progress", new { jobId, phase = "compressing", done = total, total, percent = 100 });
    }

    internal static void Extract(BrokerContext? ctx, string jobId, string zipPath, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        long total = archive.Entries.Sum(e => e.Length), done = 0;
        var lastEmit = Stopwatch.StartNew();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip 밖 경로(../) 차단
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            done += entry.Length;
            if (ctx is not null && lastEmit.ElapsedMilliseconds >= 250)
            {
                lastEmit.Restart();
                ctx.Emit("cell.progress", new { jobId, phase = "extracting", done, total, percent = total > 0 ? Math.Round(done * 100.0 / total, 1) : 0 });
            }
        }
        ctx?.Emit("cell.progress", new { jobId, phase = "extracting", done = total, total, percent = 100 });
    }

    internal static string DownloadsFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>같은 이름이 있으면 " (2)", " (3)" … (기존 폴더는 절대 지우지 않는다)</summary>
    internal static string UniqueFolder(string root, string name)
    {
        var candidate = Path.Combine(root, name);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++) candidate = Path.Combine(root, $"{name} ({i})");
        return candidate;
    }
}
