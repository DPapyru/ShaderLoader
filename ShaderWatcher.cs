using System.ComponentModel;
using Microsoft.Xna.Framework.Graphics;

namespace ShaderLoader;

/// <summary>
/// 监控 .fx Shader 文件的变更，并在可配置的防抖间隔后自动触发重新编译。
/// 防止在快速连续保存时触发多次编译。
/// </summary>
public class ShaderWatcher : IDisposable
{
    private readonly string _fxPath;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly Action<Effect> _onReloaded;
    private readonly Func<string, Effect> _reloadFunc;
    private readonly int _debounceMs;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;
    private readonly object _lock = new();

    /// <summary>
    /// 获取或设置用于将事件处理调用封送到目标线程的对象。
    /// 设置后，<c>onReloaded</c> 回调在拥有此对象的线程上调用
    /// （例如，通过 WindowsFormsSynchronizationContext 或类似机制绑定到游戏主线程）。
    /// 该值也会传递给底层的 <see cref="FileSystemWatcher.SynchronizingObject"/>。
    /// </summary>
    public ISynchronizeInvoke? SynchronizingObject
    {
        get => _synchronizingObject;
        set
        {
            _synchronizingObject = value;
            lock (_lock)
            {
                if (_watcher != null)
                    _watcher.SynchronizingObject = value;
            }
        }
    }
    private ISynchronizeInvoke? _synchronizingObject;

    /// <summary>
    /// 初始化 <see cref="ShaderWatcher"/> 类的新实例。
    /// </summary>
    /// <param name="fxPath">要监控的 .fx Shader 文件路径。</param>
    /// <param name="onReloaded">在游戏线程上调用的重载完成回调。
    /// 新编译的 <see cref="Effect"/> 作为参数传入。</param>
    /// <param name="reloadFunc">执行实际重新编译的函数。
    /// 通常调用 <see cref="DynamicEffectLoader.Load(string, string)"/> 并传入 Shader 路径。</param>
    /// <param name="debounceMs">防抖间隔（毫秒，默认 500）。
    /// 在此窗口内发生的文件变更事件会重置计时器。</param>
    /// <exception cref="ArgumentNullException">任意必要参数为 null 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="fxPath"/> 无法解析到目录时抛出。</exception>
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
        _directory = Path.GetDirectoryName(_fxPath)
            ?? throw new ArgumentException("Cannot determine directory from fxPath.", nameof(fxPath));
        _fileName = Path.GetFileName(_fxPath);
        _onReloaded = onReloaded;
        _reloadFunc = reloadFunc;
        _debounceMs = debounceMs;

        // 创建计时器并保持停止状态；首次文件变更时启动
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 开始监控 .fx 文件的变更。
    /// 如果已在运行，则不做任何操作。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已被释放时抛出。</exception>
    public void Start()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_watcher != null)
                return; // 已经启动

            _watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false, // 在完成事件绑定后启用
                SynchronizingObject = _synchronizingObject,
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            // 开始触发事件
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>
    /// 停止监控 .fx 文件。实例可通过 <see cref="Start"/> 重新启动。
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Dispose();
                _watcher = null;
            }

            // 取消任何待处理的防抖操作
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 释放此 watcher 持有的所有资源。释放后，实例无法重新启动。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            // 先停止 watcher（释放 FileSystemWatcher）
            Stop();

            // 释放防抖计时器
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    /// <summary>
    /// 处理文件系统的 Changed/Created 事件。
    /// 重置防抖计时器，使快速连续保存仅触发一次编译。
    /// </summary>
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed || _debounceTimer == null)
                return;

            // 重置计时器：debounceMs 内的后续变更将不断推迟
            _debounceTimer.Change(_debounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 防抖计时器回调。执行重载函数，成功后调用 onReloaded 回调。
    /// 失败时记录错误，保留旧的 Effect 不变。
    /// </summary>
    private void OnDebounceTimerElapsed(object? state)
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

        // 调用回调，如果有同步对象则进行封送处理
        if (_synchronizingObject != null && _synchronizingObject.InvokeRequired)
        {
            _synchronizingObject.BeginInvoke(new Action(() => _onReloaded(newEffect)), null);
        }
        else
        {
            _onReloaded(newEffect);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ShaderWatcher));
    }
}
