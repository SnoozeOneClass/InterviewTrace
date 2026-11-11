using System.Diagnostics;
using System.Globalization;
using InterviewTrace.Core;

namespace InterviewTrace.App;

internal sealed class MainForm : Form
{
    private readonly AppSettingsStore _settingsStore = new();
    private AppSettings _settings = new();
    private RecordRepository? _repository;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(6) };
    private readonly AudioPlayback _playback = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "准备就绪", ForeColor = Color.DimGray, Padding = new Padding(10, 5, 10, 8) };

    private readonly ComboBox _microphones = NewCombo();
    private readonly ComboBox _outputs = NewCombo();
    private readonly TextBox _currentDirectory = new() { Dock = DockStyle.Fill };
    private readonly TextBox _newCompany = new() { Dock = DockStyle.Fill };
    private readonly TextBox _newPosition = new() { Dock = DockStyle.Fill };
    private readonly TextBox _newRound = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _micMeter = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill, Height = 18 };
    private readonly ProgressBar _systemMeter = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill, Height = 18 };
    private readonly Label _recordState = new() { AutoSize = true, Text = "未录音", Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Padding = new Padding(3, 5, 3, 5) };
    private readonly Button _refreshDevices = Button("刷新设备");
    private readonly Button _previewButton = Button("试录 5 秒");
    private readonly Button _previewMic = Button("回听试录：我的声音");
    private readonly Button _previewSystem = Button("回听试录：系统声音");
    private readonly Button _start = Button("开始录音");
    private readonly Button _stop = Button("停止并保存");
    private readonly Button _browseCurrent = Button("选择文件夹");

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Top, Width = 760, Height = 175, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White
    };
    private readonly TextBox _company = new() { Dock = DockStyle.Fill };
    private readonly TextBox _position = new() { Dock = DockStyle.Fill };
    private readonly TextBox _round = new() { Dock = DockStyle.Fill };
    private readonly TextBox _date = new() { Dock = DockStyle.Fill, PlaceholderText = "选填，例如 2026-11-10" };
    private readonly TextBox _historyPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _transcript = new()
    {
        Dock = DockStyle.Top, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Width = 760, Height = 185, Font = new Font("Microsoft YaHei UI", 9)
    };
    private readonly Label _historyStatus = new() { AutoSize = true, Text = "选择一条面试记录", Padding = new Padding(3, 5, 3, 5) };
    private readonly Button _saveMetadata = Button("保存记录信息");
    private readonly Button _playBoth = Button("回听完整录音");
    private readonly Button _playMic = Button("仅听我的声音");
    private readonly Button _playSystem = Button("仅听系统声音");
    private readonly Button _stopPlayback = Button("停止回放");
    private readonly Button _openFolder = Button("打开文件夹");
    private readonly Button _relocate = Button("重新定位文件夹");
    private readonly Button _transcribe = Button("开始 / 继续转写");
    private readonly Button _cancelTranscription = Button("取消转写");
    private readonly Button _resetTranscription = Button("重置转写进度");
    private readonly Button _copy = Button("复制文字");
    private readonly Button _export = Button("导出文字");
    private readonly ProgressBar _transcribeProgress = new() { Minimum = 0, Maximum = 100, Width = 220, Height = 19 };

    private readonly TextBox _defaultDirectory = new() { Dock = DockStyle.Fill };
    private readonly TextBox _databasePath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "留空表示不修改已保存的密钥" };
    private readonly TextBox _apiEndpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _apiModel = new() { Dock = DockStyle.Fill };
    private readonly Label _keyState = new() { AutoSize = true };
    private readonly Button _saveSettings = Button("保存设置");
    private readonly Button _deleteKey = Button("删除已保存密钥");
    private readonly Button _browseDefault = Button("选择文件夹");
    private readonly Button _browseDatabase = Button("选择索引文件");

    private DualAudioRecorder? _recorder;
    private InterviewRecord? _recordingRecord;
    private InterviewRecord? _selected;
    private CancellationTokenSource? _transcriptionCancellation;
    private bool _previewing;
    private bool _stopping;
    private string? _previewMicPath;
    private string? _previewSystemPath;
    private string? _previewFolder;
    private bool _allowClose;

    public MainForm()
    {
        Text = "InterviewTrace - 面试录音与复盘";
        MinimumSize = new Size(880, 640);
        Size = new Size(1080, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(246, 248, 251);
        InitializeUi();
        _timer.Tick += (_, _) => UpdateMeters();
        _timer.Start();
        Shown += (_, _) => InitializeData();
        FormClosing += OnFormClosing;
    }

    private void InitializeUi()
    {
        var header = new Label
        {
            Text = "InterviewTrace", Dock = DockStyle.Top, Height = 45,
            Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), Padding = new Padding(12, 7, 0, 0)
        };
        _tabs.TabPages.Add(BuildRecordingPage());
        _tabs.TabPages.Add(BuildHistoryPage());
        _tabs.TabPages.Add(BuildSettingsPage());
        Controls.Add(_tabs);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 33, Controls = { _status } });
        Controls.Add(header);
        WireEvents();
        UpdateAvailability();
    }

    private TabPage BuildRecordingPage()
    {
        var page = NewPage("录音");
        var stack = Stack();
        stack.Controls.Add(Paragraph("开始前请选择面试软件实际使用的麦克风和耳机。先试录，再听取两路声音确认。"));
        var devices = Table();
        AddRow(devices, "麦克风", _microphones);
        AddRow(devices, "耳机 / 扬声器", _outputs);
        AddRow(devices, "麦克风音量", _micMeter);
        AddRow(devices, "系统声音音量", _systemMeter);
        stack.Controls.Add(devices);
        stack.Controls.Add(Buttons(_refreshDevices, _previewButton, _previewMic, _previewSystem));
        stack.Controls.Add(Paragraph("面试信息可选，录完后也能补充。"));
        var details = Table();
        AddRow(details, "公司", _newCompany);
        AddRow(details, "岗位", _newPosition);
        AddRow(details, "面试轮次", _newRound);
        AddRow(details, "本次保存目录", WithButton(_currentDirectory, _browseCurrent));
        stack.Controls.Add(details);
        stack.Controls.Add(_recordState);
        stack.Controls.Add(Buttons(_start, _stop));
        stack.Controls.Add(Paragraph("停止录音后，原始音频保存在本地。转写需在“面试记录”中手动开始。"));
        page.Controls.Add(stack);
        return page;
    }

    private TabPage BuildHistoryPage()
    {
        var page = NewPage("面试记录");
        var stack = Stack();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "created", HeaderText = "录制时间", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "company", HeaderText = "公司", FillWeight = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "position", HeaderText = "岗位", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "round", HeaderText = "轮次", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "duration", HeaderText = "时长", FillWeight = 65 });
        stack.Controls.Add(_grid);
        stack.Controls.Add(_historyStatus);
        var details = Table();
        AddRow(details, "公司", _company);
        AddRow(details, "岗位", _position);
        AddRow(details, "面试轮次", _round);
        AddRow(details, "面试日期", _date);
        AddRow(details, "文件夹", _historyPath);
        stack.Controls.Add(details);
        stack.Controls.Add(Buttons(_saveMetadata, _openFolder, _relocate));
        stack.Controls.Add(Buttons(_playBoth, _playMic, _playSystem, _stopPlayback));
        stack.Controls.Add(Buttons(_transcribe, _cancelTranscription, _resetTranscription, _transcribeProgress));
        stack.Controls.Add(_transcript);
        stack.Controls.Add(Buttons(_copy, _export));
        page.Controls.Add(stack);
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = NewPage("设置");
        var stack = Stack();
        stack.Controls.Add(Paragraph("默认录音目录可更改。已有录音不会自动移动；记录索引保存各次录音的实际位置。"));
        var storage = Table();
        AddRow(storage, "默认保存目录", WithButton(_defaultDirectory, _browseDefault));
        AddRow(storage, "记录索引文件", WithButton(_databasePath, _browseDatabase));
        stack.Controls.Add(storage);
        stack.Controls.Add(Paragraph("转写使用阿里云百炼 Fun-ASR Flash。录音结束后手动点击转写，长录音会分段上传并按服务商规则计费。"));
        var api = Table();
        AddRow(api, "API Key", _apiKey);
        AddRow(api, "密钥状态", _keyState);
        AddRow(api, "接口地址", _apiEndpoint);
        AddRow(api, "模型名称", _apiModel);
        stack.Controls.Add(api);
        stack.Controls.Add(Paragraph("密钥使用当前 Windows 用户保护后保存在本机；不需要把密钥发送到聊天中。"));
        stack.Controls.Add(Buttons(_saveSettings, _deleteKey));
        page.Controls.Add(stack);
        return page;
    }

    private void WireEvents()
    {
        _refreshDevices.Click += (_, _) => RefreshDevices();
        _browseCurrent.Click += (_, _) => ChooseFolder(_currentDirectory);
        _browseDefault.Click += (_, _) => ChooseFolder(_defaultDirectory);
        _browseDatabase.Click += (_, _) => ChooseDatabase();
        _previewButton.Click += async (_, _) => await PreviewAsync();
        _previewMic.Click += (_, _) => PlayPaths(_previewMicPath);
        _previewSystem.Click += (_, _) => PlayPaths(_previewSystemPath);
        _start.Click += async (_, _) => await StartRecordingAsync();
        _stop.Click += async (_, _) => await StopRecordingAsync();
        _grid.SelectionChanged += (_, _) => SelectRecord();
        _saveMetadata.Click += (_, _) => SaveMetadata();
        _openFolder.Click += (_, _) => OpenFolder();
        _relocate.Click += (_, _) => Relocate();
        _playBoth.Click += (_, _) => PlayPaths(_selected?.MicrophonePath, _selected?.SystemAudioPath);
        _playMic.Click += (_, _) => PlayPaths(_selected?.MicrophonePath);
        _playSystem.Click += (_, _) => PlayPaths(_selected?.SystemAudioPath);
        _stopPlayback.Click += (_, _) => SafeUi(() => _playback.Stop());
        _transcribe.Click += async (_, _) => await TranscribeAsync();
        _cancelTranscription.Click += (_, _) => _transcriptionCancellation?.Cancel();
        _resetTranscription.Click += (_, _) => ResetTranscription();
        _copy.Click += (_, _) => SafeUi(() => { if (_transcript.TextLength > 0) Clipboard.SetText(_transcript.Text); });
        _export.Click += (_, _) => ExportTranscript();
        _saveSettings.Click += (_, _) => SaveSettings();
        _deleteKey.Click += (_, _) => DeleteKey();
    }

    private void InitializeData()
    {
        try { _settings = _settingsStore.Load(); }
        catch (Exception ex) { MessageBox.Show($"读取设置失败：{ex.Message}\n请检查 {AppPaths.ConfigDirectory}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        _currentDirectory.Text = _settings.DefaultSaveDirectory;
        _defaultDirectory.Text = _settings.DefaultSaveDirectory;
        _databasePath.Text = _settings.DatabasePath;
        _apiEndpoint.Text = _settings.ApiEndpoint;
        _apiModel.Text = _settings.ApiModel;
        _keyState.Text = _settingsStore.HasApiKey ? "已保存" : "未设置";
        try
        {
            _repository = new RecordRepository(_settings.DatabasePath);
            _repository.MarkInterruptedSessions();
            RefreshHistory();
        }
        catch (Exception ex) { SetStatus($"记录索引不可用：{ex.Message}。请在设置中选择索引文件。"); }
        RefreshDevices();
        UpdateAvailability();
    }

    private void RefreshDevices()
    {
        SafeUi(() =>
        {
            string micId = (_microphones.SelectedItem as AudioDevice)?.Id ?? _settings.MicrophoneDeviceId;
            string outputId = (_outputs.SelectedItem as AudioDevice)?.Id ?? _settings.PlaybackDeviceId;
            BindDevices(_microphones, AudioEndpoints.ListMicrophones(), micId);
            BindDevices(_outputs, AudioEndpoints.ListOutputs(), outputId);
            SetStatus(_microphones.SelectedItem is null || _outputs.SelectedItem is null
                ? "请检查并选择可用的麦克风和耳机。" : "设备已刷新；请确认与面试软件中的选择一致。");
            UpdateAvailability();
        });
    }

    private static void BindDevices(ComboBox box, IReadOnlyList<AudioDevice> devices, string selectedId)
    {
        box.Items.Clear();
        box.Items.AddRange(devices.Cast<object>().ToArray());
        int index = string.IsNullOrEmpty(selectedId) ? -1 : devices.ToList().FindIndex(d => d.Id == selectedId);
        if (index < 0 && string.IsNullOrEmpty(selectedId)) index = devices.ToList().FindIndex(d => d.IsDefault);
        if (index >= 0) box.SelectedIndex = index; // never silently replace a previously selected missing device
    }

    private async Task PreviewAsync()
    {
        if (!TryDevices(out string microphoneId, out string outputId)) return;
        _playback.Stop();
        _previewing = true;
        UpdateAvailability();
        DualAudioRecorder? preview = null;
        bool succeeded = false;
        try
        {
            CleanupPreview();
            string baseFolder = Path.GetFullPath(_currentDirectory.Text.Trim());
            string folder = Path.Combine(baseFolder, ".InterviewTrace-trial", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _previewFolder = folder;
            string micPath = Path.Combine(folder, "microphone.wav");
            string systemPath = Path.Combine(folder, "system.wav");
            preview = await DualAudioRecorder.StartAsync(microphoneId, outputId, micPath, systemPath);
            _recorder = preview;
            SetStatus("正在试录 5 秒，请说话并让耳机播放一段声音。完成后可分别回听。");
            await Task.Delay(TimeSpan.FromSeconds(5));
            await preview.StopAsync();
            if (preview.Failure is not null) throw new InvalidOperationException(preview.Failure);
            _previewMicPath = micPath;
            _previewSystemPath = systemPath;
            succeeded = true;
            SetStatus("试录完成，请分别回听我的声音和系统声音。");
        }
        catch (Exception ex) { ShowError("试录失败", ex); }
        finally
        {
            if (preview is not null) await preview.DisposeAsync();
            _recorder = null;
            _previewing = false;
            if (!succeeded) CleanupPreview();
            UpdateAvailability();
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_repository is null) { MessageBox.Show("请先在设置中选择可用的记录索引文件。", Text); return; }
        if (!TryDevices(out string microphoneId, out string outputId)) return;
        _playback.Stop();
        _stopping = true;
        UpdateAvailability();
        InterviewRecord? record = null;
        try
        {
            string baseFolder = Path.GetFullPath(_currentDirectory.Text.Trim());
            Directory.CreateDirectory(baseFolder);
            string folder = Path.Combine(baseFolder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}"[..29]);
            Directory.CreateDirectory(folder);
            record = new InterviewRecord
            {
                Company = _newCompany.Text.Trim(), Position = _newPosition.Text.Trim(), Round = _newRound.Text.Trim(),
                FolderPath = folder, MicrophonePath = Path.Combine(folder, "microphone.wav"),
                SystemAudioPath = Path.Combine(folder, "system.wav"), TranscriptPath = Path.Combine(folder, "transcript.txt"),
                Status = RecordStates.Recording
            };
            _repository.Save(record);
            _settings.MicrophoneDeviceId = microphoneId;
            _settings.PlaybackDeviceId = outputId;
            _settingsStore.Save(_settings);
            DualAudioRecorder recorder = await DualAudioRecorder.StartAsync(microphoneId, outputId,
                record.MicrophonePath, record.SystemAudioPath);
            _recordingRecord = record;
            _recorder = recorder;
            recorder.Faulted += OnRecordingFault;
            recorder.WarningRaised += OnRecordingWarning;
            SetStatus($"正在录音，文件保存在 {folder}");
            if (recorder.Failure is not null) OnRecordingFault(recorder.Failure);
            if (recorder.Warning is not null) OnRecordingWarning(recorder.Warning);
        }
        catch (Exception ex)
        {
            if (record is not null)
            {
                record.Status = RecordStates.Interrupted;
                record.LastError = ex.Message;
                try { _repository.Save(record); } catch { }
            }
            ShowError("无法开始录音", ex);
            RefreshHistory(record?.Id);
        }
        finally { _stopping = false; UpdateAvailability(); }
    }

    private void OnRecordingWarning(string message)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => SetStatus($"录音提示：{message}")));
        }
        catch (InvalidOperationException) { }
    }

    private void OnRecordingFault(string message)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(async () =>
            {
                if (_recorder is null || _previewing || _stopping) return;
                await StopRecordingAsync(message);
                MessageBox.Show($"{message}\n已尽可能保存录音，请回听检查。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }));
        }
        catch (InvalidOperationException) { }
    }

    private async Task StopRecordingAsync(string? failure = null)
    {
        if (_recorder is null || _previewing || _stopping) return;
        _stopping = true;
        DualAudioRecorder recorder = _recorder;
        InterviewRecord? record = _recordingRecord;
        UpdateAvailability();
        SetStatus("正在保存录音，请稍候……");
        try
        {
            TimeSpan duration = await recorder.StopAsync();
            if (record is not null)
            {
                record.DurationMilliseconds = (long)duration.TotalMilliseconds;
                record.Status = failure is null && recorder.Failure is null ? RecordStates.Recorded : RecordStates.Interrupted;
                record.LastError = failure ?? recorder.Failure ?? recorder.Warning ?? "";
                _repository!.Save(record);
            }
            SetStatus("录音已保存。可在“面试记录”中回听或手动转写。");
        }
        catch (Exception ex)
        {
            if (record is not null)
            {
                record.Status = RecordStates.Interrupted;
                record.LastError = ex.Message;
                try { _repository?.Save(record); } catch { }
            }
            ShowError("录音保存异常", ex);
        }
        finally
        {
            _recorder = null;
            _recordingRecord = null;
            _stopping = false;
            _recordState.Text = "未录音";
            RefreshHistory(record?.Id);
            UpdateAvailability();
        }
    }

    private void UpdateMeters()
    {
        DualAudioRecorder? recorder = _recorder;
        _micMeter.Value = (int)Math.Clamp((recorder?.MicrophoneLevel ?? 0) * 100, 0, 100);
        _systemMeter.Value = (int)Math.Clamp((recorder?.SystemLevel ?? 0) * 100, 0, 100);
        if (recorder is not null && !_previewing) _recordState.Text = $"正在录音  {FormatDuration(recorder.Elapsed)}";
    }

    private void RefreshHistory(string? selectId = null)
    {
        if (_repository is null) return;
        try
        {
            selectId ??= _selected?.Id;
            _grid.Rows.Clear();
            foreach (InterviewRecord record in _repository.List())
            {
                int index = _grid.Rows.Add(record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    record.Company, record.Position, record.Round, record.Status,
                    FormatDuration(TimeSpan.FromMilliseconds(record.DurationMilliseconds)));
                _grid.Rows[index].Tag = record;
                if (record.Id == selectId) _grid.Rows[index].Selected = true;
            }
            if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
            SelectRecord();
        }
        catch (Exception ex) { SetStatus($"读取记录失败：{ex.Message}"); }
    }

    private void SelectRecord()
    {
        _selected = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as InterviewRecord : null;
        InterviewRecord? record = _selected;
        _company.Text = record?.Company ?? "";
        _position.Text = record?.Position ?? "";
        _round.Text = record?.Round ?? "";
        _date.Text = record?.InterviewDate ?? "";
        _historyPath.Text = record?.FolderPath ?? "";
        _historyStatus.Text = record is null ? "选择一条面试记录" : $"{record.Status}  {record.LastError}";
        try { _transcript.Text = record is not null && File.Exists(record.TranscriptPath) ? File.ReadAllText(record.TranscriptPath) : ""; }
        catch (IOException ex) { _transcript.Text = $"无法读取转写文字：{ex.Message}"; }
        UpdateAvailability();
    }

    private void SaveMetadata() => SafeUi(() =>
    {
        if (_selected is null || _repository is null) return;
        _selected.Company = _company.Text.Trim(); _selected.Position = _position.Text.Trim();
        _selected.Round = _round.Text.Trim(); _selected.InterviewDate = _date.Text.Trim();
        _repository.Save(_selected);
        RefreshHistory(_selected.Id);
        SetStatus("面试信息已保存。");
    });

    private void OpenFolder() => SafeUi(() =>
    {
        if (_selected is null) return;
        if (!Directory.Exists(_selected.FolderPath)) throw new DirectoryNotFoundException("记录文件夹不存在，可使用“重新定位文件夹”。");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_selected.FolderPath}\"") { UseShellExecute = true });
    });

    private void Relocate() => SafeUi(() =>
    {
        if (_selected is null || _repository is null) return;
        using var dialog = new FolderBrowserDialog { Description = "选择包含 microphone.wav 和 system.wav 的面试记录文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string mic = Path.Combine(dialog.SelectedPath, "microphone.wav");
        string system = Path.Combine(dialog.SelectedPath, "system.wav");
        if (!File.Exists(mic) || !File.Exists(system)) throw new FileNotFoundException("所选文件夹中未找到两路录音，请选择正确的面试记录文件夹。");
        _selected.FolderPath = dialog.SelectedPath; _selected.MicrophonePath = mic; _selected.SystemAudioPath = system;
        _selected.TranscriptPath = Path.Combine(dialog.SelectedPath, "transcript.txt");
        _repository.Save(_selected);
        RefreshHistory(_selected.Id);
        SetStatus("记录文件夹已重新定位。");
    });

    private void PlayPaths(params string?[] paths) => SafeUi(() =>
    {
        string[] actual = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
        _playback.Play(actual, (_outputs.SelectedItem as AudioDevice)?.Id);
        SetStatus("正在回放录音。");
    });

    private async Task TranscribeAsync()
    {
        if (_selected is null || _repository is null) return;
        InterviewRecord record = _selected;
        if (record.Status == RecordStates.Transcribed && File.Exists(record.TranscriptPath))
        {
            MessageBox.Show("该记录已经转写完成。若需要重新识别，请先使用“重置转写进度”。", Text);
            return;
        }
        string? key;
        try { key = _settingsStore.ReadApiKey(); }
        catch (Exception ex) { ShowError("读取 API Key 失败", ex); return; }
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("请先在“设置”中填写并保存阿里云百炼 API Key。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _tabs.SelectedIndex = 2;
            return;
        }
        _playback.Stop();
        _transcriptionCancellation = new CancellationTokenSource();
        record.Status = RecordStates.Transcribing;
        record.LastError = "";
        try { _repository.Save(record); }
        catch (Exception ex) { _transcriptionCancellation.Dispose(); _transcriptionCancellation = null; ShowError("无法保存转写状态", ex); return; }
        UpdateAvailability();
        var progress = new Progress<TranscriptionProgress>(p =>
        {
            _transcribeProgress.Value = p.Total == 0 ? 0 : Math.Clamp(p.Completed * 100 / p.Total, 0, 100);
            SetStatus(p.Message);
        });
        var service = new TranscriptionService(_http);
        try
        {
            try
            {
                await service.RunAsync(record, _settings, key, false, progress, _transcriptionCancellation.Token);
            }
            catch (UncertainTranscriptionException ex)
            {
                if (MessageBox.Show($"{ex.Message}\n\n是否继续重试？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    throw new OperationCanceledException("用户取消了可能重复计费的重试。");
                await service.RunAsync(record, _settings, key, true, progress, _transcriptionCancellation.Token);
            }
            record.Status = RecordStates.Transcribed;
            record.LastError = "";
            SetStatus("转写完成，文字已保存到面试记录文件夹。");
        }
        catch (OperationCanceledException)
        {
            record.Status = RecordStates.TranscriptionInterrupted;
            record.LastError = "转写已取消；如请求已发出，重试前需确认可能的重复计费。";
            SetStatus(record.LastError);
        }
        catch (Exception ex)
        {
            record.Status = RecordStates.TranscriptionInterrupted;
            record.LastError = ex.Message;
            ShowError("转写未完成，原始录音仍保留", ex);
        }
        finally
        {
            try { _repository.Save(record); } catch (Exception ex) { ShowError("无法保存转写状态", ex); }
            _transcriptionCancellation.Dispose();
            _transcriptionCancellation = null;
            RefreshHistory(record.Id);
            UpdateAvailability();
        }
    }

    private void ResetTranscription() => SafeUi(() =>
    {
        if (_selected is null || _repository is null) return;
        string checkpoint = Path.Combine(_selected.FolderPath, ".transcription-checkpoint.json");
        if (!File.Exists(checkpoint)) { MessageBox.Show("该记录没有转写进度。", Text); return; }
        if (MessageBox.Show("将备份已有进度和文字。下次开始转写会重新提交音频并可能再次计费，是否继续？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        string suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        if (File.Exists(_selected.TranscriptPath)) File.Copy(_selected.TranscriptPath, Path.Combine(_selected.FolderPath, $"transcript-backup-{suffix}.txt"));
        File.Move(checkpoint, Path.Combine(_selected.FolderPath, $"transcription-checkpoint-backup-{suffix}.json"));
        _selected.Status = RecordStates.Recorded; _selected.LastError = "";
        _repository.Save(_selected);
        RefreshHistory(_selected.Id);
        SetStatus("转写进度已备份并重置，尚未发送新请求。");
    });

    private void ExportTranscript() => SafeUi(() =>
    {
        if (_selected is null || !File.Exists(_selected.TranscriptPath)) throw new FileNotFoundException("当前记录还没有转写文字。");
        using var dialog = new SaveFileDialog { Title = "导出转写文字", Filter = "文本文件 (*.txt)|*.txt", FileName = "面试转写.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_selected.TranscriptPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(_selected.TranscriptPath, dialog.FileName, overwrite: true);
        SetStatus("文字已导出。");
    });

    private void SaveSettings() => SafeUi(() =>
    {
        string newDirectory = Path.GetFullPath(_defaultDirectory.Text.Trim());
        string newDatabase = Path.GetFullPath(_databasePath.Text.Trim());
        if (!Uri.TryCreate(_apiEndpoint.Text.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("转写接口必须是有效的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(_apiModel.Text)) throw new ArgumentException("请填写转写模型名称。");
        bool databaseChanged = !string.Equals(newDatabase, _settings.DatabasePath, StringComparison.OrdinalIgnoreCase);
        if (databaseChanged && MessageBox.Show("切换记录索引只会显示所选索引里的记录，不会移动或删除旧录音。继续吗？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        RecordRepository repository = databaseChanged || _repository is null ? new RecordRepository(newDatabase) : _repository;
        var next = new AppSettings
        {
            DefaultSaveDirectory = newDirectory, DatabasePath = newDatabase,
            MicrophoneDeviceId = _settings.MicrophoneDeviceId, PlaybackDeviceId = _settings.PlaybackDeviceId,
            ApiEndpoint = uri.ToString(), ApiModel = _apiModel.Text.Trim()
        };
        if (!string.IsNullOrWhiteSpace(_apiKey.Text)) _settingsStore.SaveApiKey(_apiKey.Text);
        _settingsStore.Save(next);
        _settings = next; _repository = repository; _currentDirectory.Text = newDirectory; _apiKey.Clear();
        _keyState.Text = _settingsStore.HasApiKey ? "已保存" : "未设置";
        RefreshHistory();
        SetStatus("设置已保存。");
    });

    private void DeleteKey() => SafeUi(() =>
    {
        if (MessageBox.Show("删除本机保存的 API Key？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settingsStore.DeleteApiKey(); _apiKey.Clear(); _keyState.Text = "未设置"; SetStatus("已删除保存的 API Key。");
    });

    private void ChooseDatabase()
    {
        using var dialog = new SaveFileDialog { Title = "选择记录索引文件", Filter = "SQLite 文件 (*.db)|*.db", FileName = "history.db", OverwritePrompt = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) _databasePath.Text = dialog.FileName;
    }

    private void ChooseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { Description = "选择面试文件保存目录", UseDescriptionForTitle = true };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private bool TryDevices(out string microphone, out string output)
    {
        microphone = (_microphones.SelectedItem as AudioDevice)?.Id ?? "";
        output = (_outputs.SelectedItem as AudioDevice)?.Id ?? "";
        if (microphone.Length > 0 && output.Length > 0) return true;
        MessageBox.Show("请选择面试使用的麦克风和耳机 / 扬声器。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void UpdateAvailability()
    {
        bool active = _recorder is not null || _previewing || _stopping;
        bool transcribing = _transcriptionCancellation is not null;
        bool idle = !active && !transcribing;
        bool hasRecord = _selected is not null;
        _microphones.Enabled = _outputs.Enabled = _refreshDevices.Enabled = _browseCurrent.Enabled = idle;
        _currentDirectory.Enabled = _newCompany.Enabled = _newPosition.Enabled = _newRound.Enabled = idle;
        _previewButton.Enabled = idle && _microphones.SelectedItem is not null && _outputs.SelectedItem is not null;
        _previewMic.Enabled = idle && _previewMicPath is not null;
        _previewSystem.Enabled = idle && _previewSystemPath is not null;
        _start.Enabled = idle && _repository is not null && _microphones.SelectedItem is not null && _outputs.SelectedItem is not null;
        _stop.Enabled = _recorder is not null && !_previewing && !_stopping;
        _grid.Enabled = idle;
        foreach (Button button in new[] { _saveMetadata, _playBoth, _playMic, _playSystem, _openFolder, _relocate, _transcribe, _resetTranscription, _copy, _export })
            button.Enabled = idle && hasRecord;
        _stopPlayback.Enabled = idle;
        _cancelTranscription.Enabled = transcribing;
        _saveSettings.Enabled = _deleteKey.Enabled = _browseDefault.Enabled = _browseDatabase.Enabled = idle;
        _defaultDirectory.Enabled = _databasePath.Enabled = _apiKey.Enabled = _apiEndpoint.Enabled = _apiModel.Enabled = idle;
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) { CleanupBeforeExit(); return; }
        if (_transcriptionCancellation is not null)
        {
            e.Cancel = true;
            MessageBox.Show("请等待转写结束，或先点击“取消转写”。", Text);
            return;
        }
        if (_previewing || _stopping) { e.Cancel = true; MessageBox.Show("请等待当前操作完成。", Text); return; }
        if (_recorder is not null)
        {
            e.Cancel = true;
            if (MessageBox.Show("正在录音。是否停止并保存后退出？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await StopRecordingAsync();
            _allowClose = true;
            Close();
            return;
        }
        CleanupBeforeExit();
    }

    private void CleanupBeforeExit()
    {
        _timer.Stop();
        _playback.Dispose();
        CleanupPreview();
        _http.Dispose();
    }

    private void CleanupPreview()
    {
        string? folder = _previewFolder;
        _previewFolder = _previewMicPath = _previewSystemPath = null;
        if (folder is null) return;
        string full = Path.GetFullPath(folder);
        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(Path.GetFileName(parent), ".InterviewTrace-trial", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        try
        {
            foreach (string name in new[] { "microphone.wav", "system.wav" })
            {
                string path = Path.Combine(full, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(full)) Directory.Delete(full, recursive: false);
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false);
        }
        catch (IOException) { /* Only app-created temporary files are considered for cleanup. */ }
        catch (UnauthorizedAccessException) { }
    }

    private void SetStatus(string message) => _status.Text = message;
    private void ShowError(string title, Exception ex) { SetStatus($"{title}：{ex.Message}"); MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    private void SafeUi(Action action) { try { action(); } catch (Exception ex) { ShowError("操作失败", ex); } }
    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    private static ComboBox NewCombo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 700 };
    private static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 32), Margin = new Padding(3, 3, 8, 3) };
    private static Label Paragraph(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(950, 0), Padding = new Padding(3, 7, 3, 7), ForeColor = Color.FromArgb(70, 78, 91) };
    private static TabPage NewPage(string title) => new(title) { AutoScroll = true, BackColor = Color.White, Padding = new Padding(12) };
    private static FlowLayoutPanel Stack() => new() { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };
        panel.Controls.AddRange(controls);
        return panel;
    }
    private static TableLayoutPanel Table()
    {
        var table = new TableLayoutPanel { AutoSize = true, MinimumSize = new Size(760, 0), ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }
    private static void AddRow(TableLayoutPanel table, string name, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) }, 0, row);
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
    }
    private static TableLayoutPanel WithButton(Control input, Button button)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(input, 0, 0); row.Controls.Add(button, 1, 0);
        return row;
    }
}
