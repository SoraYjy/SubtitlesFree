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
    private string _runFormat = "srt"; // 步骤条按最近一次运行格式构建（txt 无对齐/翻译/断句步）
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
    [ObservableProperty] private bool _useMirror = true;
    [ObservableProperty] private string _pythonPath = "";
    [ObservableProperty] private int _maxChars = 18;
    [ObservableProperty] private int _absorbChars = 4;

    // ---- 懂你意思（LLM 修正字幕，DeepSeek）
    [ObservableProperty] private bool _llmFixEnabled;
    [ObservableProperty] private string _llmModel = "deepseek-flash";
    [ObservableProperty] private string _llmApiKey = "";
    /// <summary>修正 prompt；空 = 用内置默认（首次启用时预填默认，改动可见可回退）。</summary>
    [ObservableProperty] private string _llmPrompt = "";
    /// <summary>视频文案草稿：随视频更换，会话内有效不持久化。</summary>
    [ObservableProperty] private string _draftText = "";

    // ---- 热词组（下拉 = 各组 + 「不使用」哨兵；编辑框绑定当前组）
    private const string NoneHotwordLabel = "（不使用热词）";
    private readonly HotwordSet _noneHotwordSet = new() { Name = NoneHotwordLabel };
    public ObservableCollection<HotwordSet> HotwordSetItems { get; } = new();
    [ObservableProperty] private HotwordSet? _selectedHotwordSet;
    [ObservableProperty] private string _hotwordsText = "";
    /// <summary>当前是否选中了真实热词组（「不使用」时编辑框禁用）。</summary>
    [ObservableProperty] private bool _hasActiveHotwordSet;

    /// <summary>large-v3-turbo 不支持翻译引擎，双语模式下英文行将是中文回写（内联警告，不禁止选择）。</summary>
    [ObservableProperty] private bool _bilingualTurboWarning;

    public ObservableCollection<StepItem> Steps { get; } = new();

    /// <summary>环境状态条逐项 ✅/✗（spec §6），每次「检测环境」后重建。</summary>
    public ObservableCollection<EnvItem> EnvItems { get; } = new();

    public IReadOnlyList<string> ModelOptions { get; } = ["large-v3-turbo", "large-v3", "small"];
    public IReadOnlyList<string> ComputeTypeOptions { get; } = ["float16", "int8_float16"];
    public IReadOnlyList<string> LlmModelOptions { get; } = ["deepseek-flash", "deepseek-v4-pro"];

    public MainViewModel()
    {
        Model = _svc.Settings.Model;
        LanguageMode = _svc.Settings.LanguageMode;
        ComputeType = _svc.Settings.ComputeType;
        UseMirror = _svc.Settings.UseMirror;
        PythonPath = _svc.Settings.PythonPath;
        MaxChars = _svc.Settings.MaxChars;
        AbsorbChars = _svc.Settings.AbsorbChars;
        LlmFixEnabled = _svc.Settings.LlmFixEnabled;
        LlmModel = _svc.Settings.LlmModel;
        LlmApiKey = _svc.Settings.LlmApiKey;
        LlmPrompt = _svc.Settings.LlmPrompt;
        foreach (HotwordSet h in _svc.Settings.HotwordSets) HotwordSetItems.Add(h);
        HotwordSetItems.Add(_noneHotwordSet);
        SelectedHotwordSet = _svc.Settings.HotwordSets
            .FirstOrDefault(h => h.Name == _svc.Settings.ActiveHotwordSet) ?? _noneHotwordSet;
        BuildSteps();
        UpdateBilingualTurboWarning();
        _ = CheckEnv();
    }

    /// <summary>与 Steps 平行的阶段名列表（引擎 stage 事件 → 步骤条定位）。</summary>
    private readonly List<string> _stages = [];

    private void BuildSteps()
    {
        _stages.Clear();
        Steps.Clear();
        void Add(string stage, string title)
        {
            _stages.Add(stage);
            Steps.Add(new StepItem(title));
        }
        if (_runFormat == "txt")
        {
            Add("load_model", "加载模型");
            Add("vad", "VAD 分块");
            Add("transcribe", "语音识别");
            Add("write", "写入 TXT");
        }
        else
        {
            Add("load_model", "加载模型");
            Add("vad", "VAD 切分");
            Add("transcribe", "语音识别");
            Add("align", "音素对齐");
            if (LanguageMode == "bilingual") Add("translate", "英文翻译");
            Add("write", "写入 SRT");
            if (LlmFixEnabled) Add("llm_fix", "LLM 修正");
        }
    }

    partial void OnModelChanged(string value) { _svc.Settings.Model = value; _svc.SaveSettings(); UpdateBilingualTurboWarning(); }
    partial void OnLanguageModeChanged(string value)
    {
        _svc.Settings.LanguageMode = value;
        BuildSteps();
        _svc.SaveSettings();
        UpdateBilingualTurboWarning();
    }
    partial void OnComputeTypeChanged(string value) { _svc.Settings.ComputeType = value; _svc.SaveSettings(); }
    partial void OnUseMirrorChanged(bool value) { _svc.Settings.UseMirror = value; _svc.SaveSettings(); }
    partial void OnPythonPathChanged(string value) { _svc.Settings.PythonPath = value; _svc.SaveSettings(); }
    partial void OnMaxCharsChanged(int value) { _svc.Settings.MaxChars = value; _svc.SaveSettings(); }
    partial void OnAbsorbCharsChanged(int value) { _svc.Settings.AbsorbChars = value; _svc.SaveSettings(); }

    partial void OnLlmFixEnabledChanged(bool value)
    {
        _svc.Settings.LlmFixEnabled = value;
        _svc.SaveSettings();
        // 首次启用预填默认 prompt：改动从可见的基线开始，不猜引擎里藏着什么
        if (value && string.IsNullOrWhiteSpace(LlmPrompt)) LlmPrompt = LlmFixDefaults.Prompt;
        BuildSteps();
    }
    partial void OnLlmModelChanged(string value) { _svc.Settings.LlmModel = value; _svc.SaveSettings(); }
    partial void OnLlmApiKeyChanged(string value) { _svc.Settings.LlmApiKey = value; _svc.SaveSettings(); }
    partial void OnLlmPromptChanged(string value) { _svc.Settings.LlmPrompt = value; _svc.SaveSettings(); }

    partial void OnSelectedHotwordSetChanged(HotwordSet? value)
    {
        bool none = value is null || ReferenceEquals(value, _noneHotwordSet);
        HasActiveHotwordSet = !none;
        HotwordsText = none ? "" : value!.Words;
        _svc.Settings.ActiveHotwordSet = none ? "" : value!.Name;
        _svc.SaveSettings();
    }

    partial void OnHotwordsTextChanged(string value)
    {
        // 编辑框改动写回当前组（选「不使用」时不写）；文本由切组刷新引起的相同值不重复保存
        if (SelectedHotwordSet is { } sel && !ReferenceEquals(sel, _noneHotwordSet) && sel.Words != value)
        {
            sel.Words = value;
            _svc.SaveSettings();
        }
    }

    [RelayCommand]
    private void AddHotwordSet()
    {
        var dlg = new Views.InputBoxWindow("新增热词组", "组名（按领域起，如：三角洲行动、电脑装机）");
        if (dlg.ShowDialog() != true) return;
        string name = dlg.InputValue;
        if (name.Length == 0 || name == NoneHotwordLabel
            || _svc.Settings.HotwordSets.Any(h => h.Name == name))
        {
            MessageBox.Show("组名不能为空、不能与已有组重复。", "新增热词组");
            return;
        }
        var set = new HotwordSet { Name = name };
        _svc.Settings.HotwordSets.Add(set);
        HotwordSetItems.Insert(HotwordSetItems.Count - 1, set); // 哨兵恒在末位
        SelectedHotwordSet = set; // 触发持久化
    }

    [RelayCommand]
    private void DeleteHotwordSet()
    {
        if (SelectedHotwordSet is not { } sel || ReferenceEquals(sel, _noneHotwordSet)) return;
        if (MessageBox.Show($"删除热词组「{sel.Name}」？（不影响其他组）", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _svc.Settings.HotwordSets.Remove(sel);
        HotwordSetItems.Remove(sel);
        SelectedHotwordSet = _noneHotwordSet; // 删的是当前组 → 切回「不使用」并持久化
    }

    private void UpdateBilingualTurboWarning()
        => BilingualTurboWarning = Model == "large-v3-turbo" && LanguageMode == "bilingual";

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
    private Task Generate() => RunEngineAsync("srt");

    [RelayCommand]
    private Task Transcribe() => RunEngineAsync("txt");

    /// <summary>生成字幕 / 转写文本共用主体：format 决定输出扩展名、请求格式与步骤条。</summary>
    private async Task RunEngineAsync(string format)
    {
        if (IsRunning || !File.Exists(VideoPath)) return;
        if (_env is { AllOk: false })
        {
            StatusText = "环境未就绪：点「检测环境」按提示修复";
            return;
        }
        string ext = format == "txt" ? ".txt" : ".srt";
        string hotwordsFlat = HotwordLibrary.ActiveWords(_svc.Settings);
        // 懂你意思只服务 SRT（txt 无时间轴可修）；启用则 Key 与文案必填，缺了就地拦下
        bool llmFix = format == "srt" && LlmFixEnabled;
        if (llmFix && (LlmApiKey.Trim().Length == 0 || DraftText.Trim().Length == 0))
        {
            StatusText = "懂你意思：请先填写 DeepSeek Key 和视频文案";
            return;
        }
        var req = new EngineRequest(
            VideoPath, Path.ChangeExtension(VideoPath, ext),
            Model, ComputeType, LanguageMode, hotwordsFlat, UseMirror,
            Math.Clamp(MaxChars, 6, 40), Math.Clamp(AbsorbChars, 0, 10), format,
            LlmFix: llmFix, LlmModel: LlmModel, LlmApiKey: LlmApiKey.Trim(),
            LlmPrompt: LlmFixDefaults.Effective(LlmPrompt), LlmDraft: DraftText);

        _runFormat = format;
        BuildSteps();
        ResetRun();
        _cts = new CancellationTokenSource();
        var launcher = new EngineLauncher(_svc.ResolvePython(), _svc.EngineScriptPath);
        if (BilingualTurboWarning && format != "txt")
            AppendLog("large-v3-turbo 不支持中→英翻译，英文行将为中文回写；双语请切 large-v3", "WARN");
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
        catch (Exception ex)
        {
            StatusText = "生成失败（详见日志）";
            AppendLog($"生成异常：{ex.Message}", "ERROR");
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
                    StatusText = _runFormat == "txt"
                        ? $"完成：{d.Segments} 段文本 · 用时 {d.ElapsedSec:F0}s（视频 {d.VideoSec:F0}s）"
                        : $"完成：{d.Segments} 条字幕 · 用时 {d.ElapsedSec:F0}s（视频 {d.VideoSec:F0}s）";
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
        int idx = _stages.IndexOf(stage);
        if (idx < 0) idx = _stages.Count - 1;
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].State = i < idx ? StepState.Done : i == idx ? StepState.Active : StepState.Pending;
    }

    private void LoadPreview()
    {
        try
        {
            if (SrtPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                string text = File.ReadAllText(SrtPath);
                Preview = text.Length <= 600 ? text : text[..600] + "…";
                return;
            }
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
        EnvItems.Clear();
        foreach (EnvItem item in EnvironmentChecker.BuildItems(_env))
        {
            EnvItems.Add(item);
            if (!item.Ok) AppendLog($"{item.Label}：{item.Hint}", "WARN");
        }
        StatusText = _env.AllOk ? "环境就绪" : "环境有缺项，按红色项提示修复";
        AppendLog($"环境检测完成（{sw.Elapsed.TotalSeconds:F0}s）");
    }

    internal void AppendLog(string msg, string? level = null)
    {
        string norm = level?.ToLowerInvariant() ?? "";
        string prefix = norm switch { "warn" => "[WARN] ", "error" => "[ERROR] ", _ => "" };
        string line = $"[{DateTime.Now:HH:mm:ss}] {prefix}{msg}";
        if (norm == "warn") _svc.Logger.Warn(msg);
        else if (norm == "error") _svc.Logger.Error(msg);
        else _svc.Logger.Info(msg);
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
