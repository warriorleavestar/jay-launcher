using Amber.Core.Auth;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.Core.Launch;

/// <summary>启动失败卡上的修复按钮（旧规格 2.8；官方设计 §6.3“上一局”）。</summary>
public enum LaunchFix
{
    /// <summary>【重新登录】：微软账号的登录过期了。</summary>
    Relogin,
    /// <summary>【下载 Java N】。</summary>
    DownloadJava,
    /// <summary>【切换到镜像后重试】：连不上官方源时。</summary>
    RetryWithMirror,
    /// <summary>【重试】。</summary>
    Retry,
    /// <summary>【查看日志】。</summary>
    ViewLog,
    /// <summary>【导出错误报告】。</summary>
    ExportReport,
}

public enum LaunchFailureKind { Login, Java, Network, Other }

/// <summary>一次启动失败：原因类别和按顺序排好的修复按钮（最可能修好的在前）。</summary>
public sealed record LaunchFailure(LaunchFailureKind Kind, IReadOnlyList<LaunchFix> Fixes, int? JavaMajor = null)
{
    /// <summary>
    /// 按异常认原因。downloadSource 是设置里的下载源（auto / official / mirror）：已经在用镜像时不再建议切镜像。
    /// 每种失败都有【查看日志】【导出错误报告】。
    /// </summary>
    public static LaunchFailure Classify(Exception error, string downloadSource)
    {
        var chain = Chain(error).ToList();
        if (chain.OfType<LoginRequiredException>().Any())
            return new(LaunchFailureKind.Login, [LaunchFix.Relogin, LaunchFix.Retry, LaunchFix.ViewLog, LaunchFix.ExportReport]);
        if (chain.OfType<JavaNotFoundException>().FirstOrDefault() is { } java)
            return new(LaunchFailureKind.Java, [LaunchFix.DownloadJava, LaunchFix.Retry, LaunchFix.ViewLog, LaunchFix.ExportReport], java.Major);
        if (chain.Any(IsNetwork))
        {
            List<LaunchFix> fixes = [LaunchFix.Retry];
            if (downloadSource != "mirror") fixes.Add(LaunchFix.RetryWithMirror);
            fixes.AddRange([LaunchFix.ViewLog, LaunchFix.ExportReport]);
            return new(LaunchFailureKind.Network, fixes);
        }
        return new(LaunchFailureKind.Other, [LaunchFix.Retry, LaunchFix.ViewLog, LaunchFix.ExportReport]);
    }

    private static bool IsNetwork(Exception e) =>
        e is DownloadException or HttpRequestException or System.Net.Sockets.SocketException or TimeoutException
        // HttpClient 超时抛的是 TaskCanceledException（里面是 TimeoutException，上面已认）。
        or TaskCanceledException { InnerException: TimeoutException };

    private static IEnumerable<Exception> Chain(Exception e)
    {
        var seen = 0;
        var stack = new Stack<Exception>([e]);
        while (stack.Count > 0 && seen++ < 32)
        {
            var current = stack.Pop();
            yield return current;
            if (current is AggregateException agg) foreach (var inner in agg.InnerExceptions) stack.Push(inner);
            else if (current.InnerException is { } inner) stack.Push(inner);
        }
    }
}
