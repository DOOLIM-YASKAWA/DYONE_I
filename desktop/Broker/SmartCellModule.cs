using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DyOne.Desktop.Broker;

/// <summary>
/// Smart Cell Plugin 관리(MotoSim 실행과 별도 모듈).
/// 설치 버전은 기존 런처와 같은 순서로 읽는다: 레지스트리 Uninstall\SmartCellPlugin → SmartCell.dll 파일 버전.
/// 설치·업데이트(SHA-256 검증, 관리자 권한 설치)는 다음 단계에서 옮겨온다.
/// </summary>
public static class SmartCellModule
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SmartCellPlugin";
    private static readonly string DllPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "MOTOMAN", "MotoSimEG-VRC", "Plugins", "x64", "SmartCell.dll");

    public static void Register(BrokerRegistry registry)
    {
        registry.Register("smartcell.status", _ =>
        {
            string? registryVersion = null;
            using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey))
                registryVersion = key?.GetValue("DisplayVersion") as string;

            var dllExists = File.Exists(DllPath);
            var dllVersion = dllExists ? FileVersionInfo.GetVersionInfo(DllPath).FileVersion : null;
            var state = registryVersion is null && !dllExists ? "notInstalled"
                : registryVersion is not null && !dllExists ? "problem"
                : "installed";

            return Task.FromResult<object?>(new { state, registryVersion, dllVersion, dllPath = DllPath });
        });

        registry.Register("smartcell.install", _ =>
            throw new BrokerException("Smart Cell Plugin 설치·업데이트는 다음 단계에서 구현합니다."));
    }
}
