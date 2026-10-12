using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 연결 문자열 조립 공용 헬퍼.
/// 비밀번호를 <paramref name="pwKey"/>(예: "Gw:Password") 시크릿/환경변수로 따로 두면
/// NpgsqlConnectionStringBuilder.Password 에 원문 그대로 대입한다(';' 등 특수문자 안전).
/// </summary>
public static class ConnBuilder
{
    public static string? Build(IConfiguration config, string csName, string pwKey)
    {
        var baseConn = config.GetConnectionString(csName);
        if (string.IsNullOrWhiteSpace(baseConn)) return null;
        var pw = config[pwKey];
        if (string.IsNullOrEmpty(pw)) return baseConn;
        return new NpgsqlConnectionStringBuilder(baseConn) { Password = pw }.ConnectionString;
    }

    /// <summary>
    /// 미러 쓰기 대상 목록. 설정 "WriteTargets"(연결 문자열 이름 배열)에 적힌 각 대상을 조립한다.
    /// 미지정 시 ["DyOneWrite"] 하나. 각 대상의 비번은 "&lt;이름&gt;:Password" 시크릿/환경변수에서 읽는다.
    /// 예) WriteTargets=["DyOneWrite","DyOneWriteProd"] → 로컬 dev + 운영(.111) 동시 적재.
    /// </summary>
    public static List<(string name, string conn)> WriteTargets(IConfiguration config)
    {
        var names = config.GetSection("WriteTargets").Get<string[]>();
        if (names is null || names.Length == 0) names = ["DyOneWrite"];
        var list = new List<(string, string)>();
        foreach (var n in names)
        {
            var c = Build(config, n, n + ":Password");
            if (!string.IsNullOrWhiteSpace(c)) list.Add((n, c!));
        }
        return list;
    }
}
