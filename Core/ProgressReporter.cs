namespace MonetIconGenerator.Core;

/// <summary>
/// 进度报告回调。CLI 和 GUI 各自注入自己的实现，
/// 业务逻辑不再直接 print/更新进度条。
/// </summary>
public class ProgressReporter
{
    private readonly Action<string, double>? _onProgress;
    private readonly Action<string>? _onLog;

    public ProgressReporter(Action<string, double>? onProgress = null, Action<string>? onLog = null)
    {
        _onProgress = onProgress;
        _onLog = onLog;
    }

    /// <summary>更新进度 0.0 ~ 1.0</summary>
    public void Report(double percent, string? detail = null)
    {
        _onProgress?.Invoke(detail ?? "", Math.Clamp(percent, 0, 1));
    }

    /// <summary>普通日志消息</summary>
    public void Log(string message)
    {
        _onLog?.Invoke(message);
    }
}
