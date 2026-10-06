using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DyOne.Desktop.Broker;

/// <summary>
/// Cell 파일 관리. 기존 MotoSim Launcher 기능을 옮겨올 자리다.
/// 원칙: 원본 Cell 폴더는 건드리지 않는다. 정리·압축은 복사본에서 하고, 업로드는 서버 API로 보낸다.
/// </summary>
public static class CellModule
{
    public static void Register(BrokerRegistry registry, Window owner)
    {
        // 사용자가 고른 폴더의 .vcl 목록만 읽는다(수정·삭제 없음).
        registry.Register("cell.pickFolder", _ =>
        {
            var dialog = new OpenFolderDialog { Title = "Cell 폴더 선택 (.vcl 파일이 있는 폴더)" };
            if (dialog.ShowDialog(owner) != true) return Task.FromResult<object?>(new { canceled = true });

            var folder = dialog.FolderName;
            var vclFiles = Directory.EnumerateFiles(folder, "*.vcl", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Take(50).ToArray();
            return Task.FromResult<object?>(new { canceled = false, folder, vclFiles });
        });

        // 다음 단계: 복사본 생성 → 백업 파일 제외 규칙(*.vcl.bak, VRC.bin.bak, *_bk_날짜_시간.zip) → 압축 → 업로드 진행률 이벤트
        registry.Register("cell.upload", _ =>
            throw new BrokerException("Cell 업로드는 다음 단계에서 구현합니다(저장소: Google Drive 유지 또는 GCS 이전 결정 후)."));
    }
}
