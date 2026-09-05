using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitlesFree.App.Services;
using SubtitlesFree.Core;

namespace SubtitlesFree.App.ViewModels;

public enum StepState { Pending, Active, Done }

public partial class StepItem(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] private StepState _state;
}

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _svc = AppServices.Instance;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? _cts;
    private EnvReport? _env;

    // ---- 运行状态
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canGenerate = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "拖入 MP4 开始";
    [ObservableProperty] private string _srtPath = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _logText = "";

    // ---- 输入
    [ObservableProperty] private string _videoPath = "";
    [ObservableProperty] private string _videoInfo = "";

    // ---- 设置（双向绑定，changed 时持久化）
    [ObservableProperty] private string _model = "large-v3-turbo";
    [ObservableProperty] private string _languageMode = "zh";
    [ObservableProperty] private string _computeType = "float16";
    [ObservableProperty] private string _hotwords = "";
    [ObservableProperty] private bool _useMirror = true;
    [ObservableProperty] private string _pythonPath = "";

    public ObservableCollection<StepItem> Steps { get; } = new();

    public IReadOnlyList<string> ModelOptions { get; } = ["large-v3-turbo", "large-v3", "small"];
    public IReadOnlyList<string> ComputeTypeOptions { get; } = ["float16", "int8_float16"];

    public MainViewModel()
    {
        Model = _svc.Settings.Model;
        LanguageMode = _svc.Settings.LanguageMode;
        ComputeType = _svc.Settings.ComputeType;
        Hotwords = _svc.Settings.Hotwords;
        UseMirror = _svc.Settings.UseMirror;
        PythonPath = _svc.Settings.PythonPath;
        BuildSteps();
        _ = CheckEnv();
    }

    private void BuildSteps()
    {
        Steps.Clear();
        string[] titles = LanguageMode == "bilingual"
            ? ["加载模型", "VAD 切分", "语音识别", "音素对齐", "英文翻译", "写入 SRT"]
            : ["加载模型", "VAD 切分", "语音识别", "音素对齐", "写入 SRT"];
        foreach (string t in titles) Steps.Add(new StepItem(t));
    }

    partial void OnModelChanged(string value) { _svc.Settings.Model = value; _svc.SaveSettings(); }
    partial void OnLanguageModeChanged(string value)
    {
        _svc.Settings.LanguageMode = value;
        BuildSteps();
        _svc.SaveSettings();
    }
    partial void OnComputeTypeChanged(string value) { _svc.Settings.ComputeType = value; _svc.SaveSettings(); }
    partial void OnHotwordsChanged(string value) { _svc.Settings.Hotwords = value; _svc.SaveSettings(); }
    partial void OnUseMirrorChanged(bool value) { _svc.Settings.UseMirror = value; _svc.SaveSettings(); }
    partial void OnPythonPathChanged(string value) { _svc.Settings.PythonPath = value; _svc.SaveSettings(); }

    public void VideoDropped(string path)
    {
        if (IsRunning || !File.Exists(path)) return;
        VideoPath = path;
        long sizeMb = new FileInfo(path).Length / (1024 * 1024);
        VideoInfo = $"{Path.GetFileName(path)} · {sizeMb:N0} MB";
        StatusText = "就绪，点「生成字幕」";
        _svc.Settings.LastInputDir = Path.GetDirectoryName(path)!;
        _svc.SaveSettings();
        AppendLog($"已选择视频：{path}");
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi|所有文件|*.*",
        };
        if (Directory.Exists(_svc.Settings.LastInputDir)) dlg.InitialDirectory = _svc.Settings.LastInputDir;
        if (dlg.ShowDialog() == true) VideoDropped(dlg.FileName);
    }

    [RelayCommand]
    private async Task Generate()
    {
        if (IsRunning || !File.Exists(VideoPath)) return;
        if (_env is { AllOk: false })
        {
            StatusText = "环境未就绪：点「检测环境」按提示修复";
            return;
        }
        string hotwordsFlat = string.Join(",", Hotwords
            .Split([",", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim()).Where(w => w.Length > 0));
        var req = new EngineRequest(
            VideoPath, Path.ChangeExtension(VideoPath, ".srt"),
            Model, ComputeType, LanguageMode, hotwordsFlat, UseMirror);

        ResetRun();
        _cts = new CancellationTokenSource();
        var launcher = new EngineLauncher(_svc.ResolvePython(), _svc.EngineScriptPath);
        try
        {
            await launcher.RunAsync(req, _cts.Token, OnEngineEvent, s => AppendLog(s));
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            AppendLog("已取消（引擎进程已终止）", "WARN");
        }
        catch (EngineFailedException ex)
        {
            StatusText = "生成失败（详见日志）";
            AppendLog(ex.Message, "ERROR");
        }
        finally
        {
            IsRunning = false;
            CanGenerate = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ResetRun()
    {
        IsRunning = true;
        CanGenerate = false;
        Progress = 0;
        SrtPath = "";
        Preview = "";
        StatusText = "运行中…";
        foreach (var s in Steps) s.State = StepState.Pending;
    }

    private void OnEngineEvent(EngineEvent ev)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (ev)
            {
                case StageEvent s:
                    MarkStep(s.Stage);
                    break;
                case ProgressEvent p:
                    Progress = p.Value;
                    break;
                case LogEvent l:
                    AppendLog(l.Message, l.Level);
                    break;
                case DoneEvent d:
                    SrtPath = d.SrtPath;
                    Progress = 1;
                    foreach (var s in Steps) s.State = StepState.Done;
                    StatusText = $"完成：{d.Segments} 条字幕 · 用时 {d.ElapsedSec:F0}s（视频 {d.VideoSec:F0}s）";
                    LoadPreview();
                    break;
                case ErrorEvent e:
                    StatusText = $"失败：{e.Message}" + (string.IsNullOrWhiteSpace(e.Hint) ? "" : $" {e.Hint}");
                    AppendLog($"引擎错误 [{e.Code}] {e.Message} {e.Hint}", "ERROR");
                    break;
            }
        });
    }

    private void MarkStep(string stage)
    {
        int idx = stage switch
        {
            "load_model" => 0,
            "vad" => 1,
            "transcribe" => 2,
            "align" => 3,
            "translate" => Steps.Count - 2,
            "write" => Steps.Count - 1,
            _ => Steps.Count - 1,
        };
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].State = i < idx ? StepState.Done : i == idx ? StepState.Active : StepState.Pending;
    }

    private void LoadPreview()
    {
        try
        {
            var cues = SrtPreview.ParseFirst(SrtPath, 5);
            Preview = string.Join("\n\n", cues.Select(c =>
                $"[{c.Start:hh\\:mm\\:ss}→{c.End:hh\\:mm\\:ss}] {c.Text.Replace("\r\n", " / ")}"));
        }
        catch (Exception ex)
        {
            Preview = $"预览失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenFolder()
    {
        if (File.Exists(SrtPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SrtPath}\"")
            {
                UseShellExecute = true,
            });
    }

    [RelayCommand]
    private async Task CheckEnv()
    {
        StatusText = "检测环境…";
        var sw = Stopwatch.StartNew();
        _env = await EnvironmentChecker.CheckAsync(_svc.ResolvePython(), Model, l => AppendLog(l));
        StatusText = _env.AllOk ? "环境就绪" : "环境有缺项，见日志提示";
        AppendLog($"环境检测完成（{sw.Elapsed.TotalSeconds:F0}s）");
        if (_env is { AllOk: false })
        {
            AppendLog("修复参考：pip install torch --index-url https://download.pytorch.org/whl/cu124；"
                      + "pip install whisperx", "WARN");
        }
    }

    internal void AppendLog(string msg, string? level = null)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _svc.Logger.Info(msg);
        _dispatcher.BeginInvoke(() =>
        {
            LogText = LogText.Length == 0 ? line : LogText + "\n" + line;
            // 截断保留最近 400 行
            int idx = LogText.IndexOf('\n');
            int lines = LogText.Count(ch => ch == '\n') + 1;
            while (lines > 400 && idx >= 0)
            {
                LogText = LogText[(idx + 1)..];
                idx = LogText.IndexOf('\n');
                lines--;
            }
        });
    }
}
