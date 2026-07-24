using System;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;

namespace ShaderLoader;

/// <summary>
/// 通过轮询 LastWriteTime + 防抖 Timer 的方式监控 .fx 文件变更并触发热重载。
/// 检测和重载都在 ThreadPool 线程上执行，不阻塞游戏主循环。
/// Effect 通过 EffectFactoryOverride（内部 QueueMainThreadAction）在主线程创建。
/// </summary>
public class ShaderWatcher : IDisposable
{
    private readonly string _fxPath;
    private readonly Action<Effect> _onReloaded;
    private readonly Func<string, Effect> _reloadFunc;
    private readonly int _debounceMs;

    private Timer? _pollTimer;
    private Timer? _debounceTimer;
    private DateTime _lastWrite;
    private bool _disposed;
    private readonly object _lock = new();

    public ShaderWatcher(
        string fxPath,
        Action<Effect> onReloaded,
        Func<string, Effect> reloadFunc,
        int debounceMs = 500)
    {
        ArgumentNullException.ThrowIfNull(fxPath);
        ArgumentNullException.ThrowIfNull(onReloaded);
        ArgumentNullException.ThrowIfNull(reloadFunc);

        _fxPath = Path.GetFullPath(fxPath);
        _onReloaded = onReloaded;
        _reloadFunc = reloadFunc;
        _debounceMs = debounceMs;

        _lastWrite = File.GetLastWriteTimeUtc(_fxPath);

        // 防抖 Timer 保持停止，文件变更时启动
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_pollTimer != null)
                return;

            _pollTimer = new Timer(OnPoll, null, 0, _debounceMs / 2);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
            }

            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnPoll(object? state)
    {
        if (_disposed)
            return;

        try
        {
            DateTime current = File.GetLastWriteTimeUtc(_fxPath);
            if (current != _lastWrite)
            {
                _lastWrite = current;

                lock (_lock)
                {
                    _debounceTimer?.Change(_debounceMs, Timeout.Infinite);
                }
            }
        }
        catch (Exception ex)
        {
            DynamicEffectLoader.Logger?.Warn(
                $"[ShaderWatcher] 轮询 '{_fxPath}' 失败: {ex.Message}");
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed)
            return;

        Effect? newEffect;
        try
        {
            newEffect = _reloadFunc(_fxPath);
        }
        catch (Exception ex)
        {
            DynamicEffectLoader.Logger?.Error(
                $"[ShaderWatcher] 重载 '{_fxPath}' 失败: {ex.Message}");
            return;
        }

        if (newEffect == null)
        {
            DynamicEffectLoader.Logger?.Warn(
                $"[ShaderWatcher] 重载 '{_fxPath}' 返回 null");
            return;
        }

        DynamicEffectLoader.Logger?.Info(
            $"[ShaderWatcher] 热重载成功: {_fxPath}");

        _onReloaded(newEffect);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ShaderWatcher));
    }
}
