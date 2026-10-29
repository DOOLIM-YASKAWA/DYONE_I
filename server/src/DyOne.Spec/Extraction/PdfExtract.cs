using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DyOne.Spec.Extraction;

/// <summary>
/// PDF 추출. 표 인식이 중요해서 1순위는 Python(PyMuPDF find_tables, dyone-pilot 과 같은 규칙: python/spec_pdf.py),
/// Python 을 쓸 수 없으면 .NET(PdfPig)으로 줄 단위 텍스트만 뽑는다(표 칸 구분이 없어 조항 분해 품질이 떨어짐).
/// </summary>
internal static class PdfExtract
{
    public static (int units, List<Block> blocks, string engine) Read(byte[] data, string? pythonPath)
    {
        if (!string.IsNullOrWhiteSpace(pythonPath))
        {
            var viaPython = TryPython(data, pythonPath);
            if (viaPython is not null) return (viaPython.Value.units, viaPython.Value.blocks, "python");
        }
        var (u, b) = PdfPigLines(data);
        return (u, b, "pdfpig");
    }

    private static (int units, List<Block> blocks)? TryPython(byte[] data, string pythonPath)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "python", "spec_pdf.py");
        if (!File.Exists(script)) return null;
        try
        {
            var psi = new ProcessStartInfo(pythonPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(script);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using (var stdin = proc.StandardInput.BaseStream) stdin.Write(data, 0, data.Length);
            if (!proc.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                try { proc.Kill(true); } catch { /* 이미 종료 */ }
                return null;
            }
            if (proc.ExitCode != 0) return null;
            using var json = JsonDocument.Parse(stdoutTask.Result);
            var root = json.RootElement;
            var blocks = root.GetProperty("blocks").EnumerateArray().Select(b => new Block(
                "page", b.GetProperty("unit_index").GetInt32(), "", b.GetProperty("row").GetInt32(), b.GetProperty("table").GetInt32(),
                b.GetProperty("cells").EnumerateArray().Select(c => c.GetString() ?? "").ToList())).ToList();
            _ = stderrTask.Result;
            return (root.GetProperty("units").GetInt32(), blocks);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>PdfPig 대체: 쪽마다 단어를 줄(같은 높이)로 묶어 위→아래, 왼쪽→오른쪽 순서의 글 줄로.</summary>
    internal static (int, List<Block>) PdfPigLines(byte[] data)
    {
        var blocks = new List<Block>();
        using var pdf = PdfDocument.Open(data);
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var lines = new List<List<Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Top).ThenBy(w => w.BoundingBox.Left))
            {
                var tol = Math.Max(2.0, w.BoundingBox.Height * 0.5);
                var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= tol);
                if (line is null) lines.Add([w]);
                else line.Add(w);
            }
            var row = 0;
            foreach (var line in lines.OrderByDescending(l => l[0].BoundingBox.Bottom))
            {
                var text = SpecText.Clean(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
                foreach (var part in text.Split('\n').Where(p => p.Length > 0))
                {
                    row++;
                    blocks.Add(new Block("page", page.Number, "", row, 0, [part]));
                }
            }
        }
        return (pdf.NumberOfPages, blocks);
    }
}
