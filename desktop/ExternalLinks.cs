using System;
using System.Diagnostics;

namespace DyOne.Desktop;

public static class ExternalLinks
{
    // https 링크만 기본 브라우저로 연다. file:, javascript: 등은 무시한다.
    public static bool Open(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
