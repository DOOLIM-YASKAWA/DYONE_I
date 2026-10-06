using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace DyOne.Desktop.Broker;

/// <summary>화면에 보여줘도 되는 오류(입력 문제, 미구현 등). 그 밖의 예외는 일반 메시지로 바꿔 돌려준다.</summary>
public sealed class BrokerException(string message) : Exception(message);

/// <summary>
/// Local Broker 명령 목록(화이트리스트). 여기 등록되지 않은 명령은 모두 거부된다.
/// 보안 규칙 2·3: 만능 명령(임의 exe 실행·임의 파일 읽기)은 만들지 않고, 경로·입력값은 .NET 쪽에서 다시 검사한다.
/// </summary>
public sealed class BrokerRegistry
{
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _commands = new(StringComparer.Ordinal);

    public void Register(string command, Func<JsonElement, Task<object?>> handler) => _commands.Add(command, handler);

    public bool TryGet(string command, out Func<JsonElement, Task<object?>> handler) => _commands.TryGetValue(command, out handler!);

    public static BrokerRegistry CreateDefault(Window owner)
    {
        var registry = new BrokerRegistry();
        CellModule.Register(registry, owner);
        MotoSimModule.Register(registry);
        SmartCellModule.Register(registry);
        return registry;
    }

    internal static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
