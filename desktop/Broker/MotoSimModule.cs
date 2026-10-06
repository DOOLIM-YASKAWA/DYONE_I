using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DyOne.Desktop.Broker;

/// <summary>
/// MotoSim 실행. 기존 런처와 같이 C:\Program Files\MOTOMAN\MotoSimEG-VRC* 에서 실행 파일을 찾는다.
/// 지금은 설치 확인만 하고, 실행(.vcl 인자 전달·프로세스 정리)은 다음 단계에서 옮겨온다.
/// </summary>
public static partial class MotoSimModule
{
    private static readonly string MotomanRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MOTOMAN");

    public static void Register(BrokerRegistry registry)
    {
        registry.Register("motosim.status", _ =>
        {
            var installs = Directory.Exists(MotomanRoot)
                ? Directory.EnumerateDirectories(MotomanRoot, "MotoSimEG-VRC*")
                    .Select(dir => new { dir, exe = Path.Combine(dir, "MotoSimEG-VRC.exe") })
                    .Where(x => File.Exists(x.exe))
                    .Select(x => new { version = VersionOf(x.dir), path = x.exe })
                    .OrderByDescending(x => x.version)
                    .ToArray()
                : [];
            return Task.FromResult<object?>(new { installed = installs.Length > 0, installs });
        });

        registry.Register("motosim.launch", _ =>
            throw new BrokerException("MotoSim 실행은 다음 단계에서 구현합니다(기존 런처 기능 이식)."));
    }

    private static string VersionOf(string dir) => YearPattern().Match(Path.GetFileName(dir)) is { Success: true } m ? m.Value : Path.GetFileName(dir);

    [GeneratedRegex(@"20\d{2}")]
    private static partial Regex YearPattern();
}
