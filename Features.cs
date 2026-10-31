using System.Diagnostics;
using System.Text;

namespace AutoInput;

sealed partial class MainForm
{
    readonly ProgressBar progress = new() { Width = 460, Height = 18, Maximum = 1000 };
    readonly Label progressText = new() { AutoSize = true };
    readonly Button restore = new() { Text = "저장 위치에서 시작", AutoSize = true, Enabled = false };
    bool saveWorkEnabled = true, checkUpdatesOnStart, restoringWork;
    bool checkingUpdates, modalOpen;
    int checkpointOffset;
    string documentName = "직접 입력", inputSnapshot = "";
    long lastCheckpoint;
    readonly CancellationTokenSource lifetime = new();

    void InitializeFeatures(FlowLayoutPanel information, FlowLayoutPanel options)
    {
        information.Controls.Add(progress);
        information.Controls.Add(progressText);
        options.Controls.Add(restore);
        restore.Click += async (_, _) => await StartInput(true);
        var help = (ToolStripMenuItem)menu.Items[0];
        help.DropDownItems.Add("오류 로그 폴더 열기", null, (_, _) =>
        {
            PauseForDialog();
            try { Directory.CreateDirectory(AppPaths.LogDirectory); Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { ReportError(ex, "로그 폴더 열기"); }
        });
        help.DropDownItems.Add("업데이트 확인", null, async (_, _) => { PauseForDialog(); await CheckUpdates(true); });
        var settingsMenu = (ToolStripMenuItem)menu.Items[1];
        // Existing state objects remain the source of truth; edit them in one dialog.
        settingsMenu.DropDownItems.Clear();
        settingsMenu.Click += (_, _) => OpenSettings();
        help.DropDownItems.Add("저장 작업 삭제", null, (_, _) =>
        {
            PauseForDialog();
            if (cancellation != null) { status.Text = "완전히 중지한 뒤 저장 작업을 삭제하세요."; return; }
            if (MessageBox.Show(this, "저장된 문서 내용과 입력 위치를 삭제할까요?", "저장 작업 삭제", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try { SavedWork.Clear(); preview.Clear(); checkpointOffset = 0; saveWorkDirty = false; UpdateProgress(); }
                catch (Exception ex) { ReportError(ex, "저장 작업 삭제"); }
            }
        });
        preview.TextChanged += (_, _) =>
        {
            if (restoringWork || cancellation != null) return;
            checkpointOffset = 0;
            saveWorkDirty = true;
            UpdateProgress();
        };
        RestoreSavedWork();
        UpdateProgress();
        Shown += async (_, _) => { if (checkUpdatesOnStart) await CheckUpdates(false); };
    }

    bool saveWorkDirty;
    internal void StopForError() { cancellation?.Cancel(); SaveCheckpoint(true); }
    void RestoreSavedWork()
    {
        if (!saveWorkEnabled) return;
        try
        {
            var saved = SavedWork.Load();
            if (saved == null || saved.Text.Length == 0) return;
            restoringWork = true;
            preview.Text = saved.Text;
            checkpointOffset = saved.Offset;
            documentName = saved.DocumentName;
            status.Text = "저장 작업을 불러왔습니다. 대상 창의 커서를 맞춘 뒤 ‘저장 위치에서 시작’을 누르세요.";
        }
        catch (Exception ex) { ReportError(ex, "저장 작업 불러오기"); }
        finally { restoringWork = false; }
    }

    void SaveCheckpoint(bool force = false)
    {
        if (!saveWorkEnabled || (!force && (!saveWorkDirty || Environment.TickCount64 - lastCheckpoint < 1000))) return;
        try
        {
            string text = cancellation != null ? inputSnapshot : SavedWork.Normalize(preview.Text);
            if (text.Length == 0 || checkpointOffset >= text.Length) SavedWork.Clear();
            else new SavedWork(text, checkpointOffset, documentName).Save();
            saveWorkDirty = false;
            lastCheckpoint = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "작업 위치 저장");
            lastCheckpoint = Environment.TickCount64;
            saveWorkDirty = false;
            status.Text = "입력 위치를 저장하지 못했습니다. 도움말에서 오류 로그를 확인하세요.";
        }
    }

    void UpdateProgress()
    {
        string text = cancellation != null ? inputSnapshot : SavedWork.Normalize(preview.Text);
        int offset = Math.Clamp(checkpointOffset, 0, text.Length);
        int total = text.EnumerateRunes().Count();
        int done = text[..offset].EnumerateRunes().Count();
        progress.Value = total == 0 ? 0 : (int)((long)done * 1000 / total);
        progressText.Text = $"진행률 {(total == 0 ? 0 : 100d * done / total):F1}% · 완료 {done:N0} / 전체 {total:N0}자 · 남음 {total - done:N0}자";
        restore.Enabled = cancellation == null && offset > 0 && offset < text.Length;
    }

    void PauseForDialog()
    {
        if (cancellation != null && !paused) TogglePause();
    }

    void OpenSettings()
    {
        PauseForDialog();
        if (modalOpen) return;
        modalOpen = true;
        try
        {
            using var dialog = new Form { Text = "설정", ClientSize = new Size(530, 495), MinimumSize = new Size(530, 495),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Icon = Icon };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            CheckBox Check(string label, bool value) { var c = new CheckBox { Text = label, Checked = value, AutoSize = true }; panel.Controls.Add(c); return c; }
            var dark = Check("다크 모드", darkMode.Checked);
            var wrap = Check("미리보기 자동 줄바꿈", wordWrap.Checked);
            var top = Check("항상 위에 표시", alwaysOnTop.Checked);
            var minimize = Check("입력 시작 시 창 최소화", minimizeOnStart.Checked);
            var save = Check("중단 위치 자동 저장 (문서 내용도 이 PC에 저장)", saveWorkEnabled);
            var updates = Check("시작 시 업데이트 확인", checkUpdatesOnStart);
            panel.Controls.Add(new Label { Text = "미리보기 글자 크기", AutoSize = true });
            var size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            size.Items.AddRange(new object[] { 10, 12, 14, 16, 18, 22 }); size.SelectedItem = editorFontSize; panel.Controls.Add(size);
            panel.Controls.Add(new Label { Text = "시작 / 재개 대기(초)", AutoSize = true });
            var wait = new NumericUpDown { Minimum = 0, Maximum = delay.Maximum, Value = delay.Value, Width = 250, ThousandsSeparator = true }; panel.Controls.Add(wait);
            panel.Controls.Add(new Label { Text = "글자 간격(ms)", AutoSize = true });
            var speed = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = interval.Value, Width = 140 }; panel.Controls.Add(speed);
            var actions = new FlowLayoutPanel { AutoSize = true };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel };
            var reset = new Button { Text = "기본값", AutoSize = true };
            reset.Click += (_, _) => { dark.Checked = wrap.Checked = top.Checked = minimize.Checked = updates.Checked = false; save.Checked = true; size.SelectedItem = 12; wait.Value = 5; speed.Value = 30; };
            actions.Controls.AddRange([ok, cancel, reset]); panel.Controls.Add(actions);
            dialog.Controls.Add(panel); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            PaintControls(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            loadingSettings = true;
            try
            {
                darkMode.Checked = dark.Checked; wordWrap.Checked = wrap.Checked; alwaysOnTop.Checked = top.Checked; minimizeOnStart.Checked = minimize.Checked;
                saveWorkEnabled = save.Checked; checkUpdatesOnStart = updates.Checked;
                SetEditorFont((int)size.SelectedItem!); delay.Value = wait.Value; interval.Value = speed.Value;
            }
            finally { loadingSettings = false; }
            ApplyTheme(); SaveSettings();
            if (saveWorkEnabled) SaveCheckpoint(true);
            else { try { SavedWork.Clear(); } catch (Exception ex) { ReportError(ex, "저장 작업 삭제"); } }
        }
        finally { modalOpen = false; }
    }

    void ReportError(Exception exception, string operation)
    {
        ErrorLog.Write(exception, operation);
        if (!closing) MessageBox.Show(this, $"{operation}: {exception.Message}\n\n도움말 → 오류 로그 폴더 열기에서 기록을 확인하세요.", "오류");
    }

    async Task CheckUpdates(bool interactive)
    {
        if (checkingUpdates || closing) return;
        checkingUpdates = true;
        try
        {
            var release = await UpdateService.Fetch(lifetime.Token);
            if (closing) return;
            if (release == null) { if (interactive) MessageBox.Show(this, "공개된 정식 배포 버전이 없습니다.", "업데이트 확인"); return; }
            if (release.Version <= UpdateService.CurrentVersion) { if (interactive) MessageBox.Show(this, $"현재 버전 {UpdateService.CurrentVersion}이 최신입니다.", "업데이트 확인"); return; }
            PauseForDialog();
            if (MessageBox.Show(this, $"새 버전 {release.Tag}이 있습니다.\n현재 버전: {UpdateService.CurrentVersion}\n\nGitHub 배포 페이지를 열까요?", "업데이트 확인", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://github.com/piuswjk628/autotext-auto_writer/releases/tag/" + Uri.EscapeDataString(release.Tag)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (closing) return;
            ErrorLog.Write(ex, "업데이트 확인");
            if (interactive) MessageBox.Show(this, $"업데이트를 확인하지 못했습니다. 인터넷 연결을 확인해 주세요.\n{ex.Message}", "업데이트 확인");
        }
        finally { checkingUpdates = false; }
    }
}
