using System.Diagnostics;
using System.Text;

namespace DyOne.Spec.Draft;

/// <summary>빌드 출력 python/ 아래 도우미 스크립트 실행(사양정리 pptx 렌더러, PDF 쪽 그림)</summary>
public static class SpecPython
{
    public static string Script(string name) => Path.Combine(AppContext.BaseDirectory, "python", name);
    public static string Template(string name) => Path.Combine(AppContext.BaseDirectory, "templates", name);

    public static async Task<(int exit, byte[] stdout, string stderr)> RunAsync(string pythonPath, string script, IEnumerable<string> args,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(pythonPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Script(script));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Python을 실행하지 못했습니다.");
        using var ms = new MemoryStream();
        var outTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* 이미 종료 */ }
            throw new TimeoutException($"{script} 실행 시간이 너무 깁니다.");
        }
        await outTask;
        return (proc.ExitCode, ms.ToArray(), await errTask);
    }
}
