using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using System.Text.Json;

namespace AutoInput;

static class Program
{
    [STAThread]
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            foreach (var form in Application.OpenForms.OfType<MainForm>()) form.StopForError();
            ErrorLog.Write(e.Exception, "화면 처리 오류");
            MessageBox.Show("오류가 발생했습니다. 도움말의 오류 로그를 확인한 뒤 프로그램을 다시 실행하세요.", "문서 자동 입력");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ErrorLog.Write(ex, "처리되지 않은 오류"); };
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { ErrorLog.Write(ex, "프로그램 시작"); MessageBox.Show("프로그램을 시작하지 못했습니다. AutoInput 오류 로그를 확인해 주세요.", "문서 자동 입력"); }
    }
}

sealed partial class MainForm : Form
{
    readonly TextBox preview = new() { Multiline = true, ScrollBars = ScrollBars.Both, AcceptsTab = true, WordWrap = false, Dock = DockStyle.Fill };
    readonly NumericUpDown delay = new() { Minimum = 0, Maximum = decimal.MaxValue / 1000000, Value = 5, Width = 110, ThousandsSeparator = true };
    readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 1000, Value = 30, Width = 70 };
    readonly Label status = new() { Text = "준비됨", AutoSize = true };
    readonly Label estimate = new() { AutoSize = true };
    readonly System.Windows.Forms.Timer estimateTimer = new() { Interval = 500 };
    int remainingCharacters, activeGap;
    decimal startupRemaining;
    internal Action<string> SendCharacter = Native.TypeText;
    readonly Button start = new() { Text = "시작 (F8)", AutoSize = true };
    readonly Button choose = new() { Text = "TXT / DOCX 선택", AutoSize = true };
    readonly Button pause = new() { Text = "일시정지 (F10)", AutoSize = true, Enabled = false };
    CancellationTokenSource? cancellation;
    bool paused;
    decimal resumeAt;
    bool closing;
    readonly MenuStrip menu = new();
    readonly ToolStripMenuItem darkMode = new("다크 모드") { CheckOnClick = true };
    readonly ToolStripMenuItem wordWrap = new("자동 줄바꿈") { CheckOnClick = true };
    readonly ToolStripMenuItem alwaysOnTop = new("항상 위에 표시") { CheckOnClick = true };
    readonly ToolStripMenuItem minimizeOnStart = new("입력 시작 시 창 최소화") { CheckOnClick = true };
    readonly ToolStripMenuItem fontSizeMenu = new("글자 크기");
    int editorFontSize = 12;
    readonly List<Font> ownedEditorFonts = new();
    static string SettingsPath => Path.Combine(AppPaths.Root, "settings.json");

    public MainForm()
    {
        Text = "문서 자동 입력";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("AutoInput.app.ico"))
            if (iconStream != null) Icon = new Icon(iconStream);
        ClientSize = new Size(860, 600);
        MinimumSize = new Size(780, 450);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(choose, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        options.Controls.AddRange([new Label { Text = "시작 대기(초)", AutoSize = true }, delay,
            new Label { Text = "글자 간격(ms)", AutoSize = true }, interval, start]);
        var stop = new Button { Text = "중지 (F9 / Esc)", AutoSize = true };
        options.Controls.Add(stop);
        options.Controls.Add(pause);
        layout.Controls.Add(options, 0, 2);
        var information = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        information.Controls.Add(status);
        information.Controls.Add(estimate);
        layout.Controls.Add(information, 0, 3);
        layout.Controls.Add(new Label { Text = "시작 후 대상 창을 클릭하세요. 줄바꿈은 Enter, 탭은 Tab 키로 입력됩니다.", AutoSize = true }, 0, 4);
        Controls.Add(layout);
        BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;
        LoadSettings();
        InitializeFeatures(information, options);
        ApplyTheme();
        choose.Click += (_, _) => LoadDocument();
        start.Click += async (_, _) => await StartInput();
        stop.Click += (_, _) => cancellation?.Cancel();
        pause.Click += (_, _) => TogglePause();
        estimateTimer.Tick += (_, _) => { UpdateEstimate(); UpdateProgress(); SaveCheckpoint(); };
        estimateTimer.Start();
        preview.TextChanged += (_, _) => UpdateEstimate();
        UpdateEstimate();
        FormClosing += (_, _) => { SaveCheckpoint(true); estimateTimer.Stop(); SaveSettings(); closing = true; lifetime.Cancel(); cancellation?.Cancel(); };
    }

    void BuildMenu()
    {
        var help = new ToolStripMenuItem("도움말");
        help.DropDownItems.Add("사용 방법 / 단축키", null, (_, _) =>
        {
            if (cancellation != null && !paused) TogglePause();
            using var dialog = new Form { Text = "도움말", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(570, 390), MinimizeBox = false, MaximizeBox = false, Icon = Icon };
            var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("맑은 고딕", 11),
                Text = "1. TXT / DOCX 파일을 선택하고 내용을 확인합니다.\r\n2. F8을 누른 뒤 대기 시간 안에 대상 입력창을 클릭합니다.\r\n\r\nF8: 처음부터 입력 시작\r\nF10: 일시정지 / 같은 위치에서 이어 입력\r\nF9 또는 Esc: 완전 중지\r\n\r\n재개 시에도 설정된 대기 시간 후 입력합니다.\r\n글자 간격은 ms 단위이며 값이 클수록 느려집니다.\r\n\r\n줄바꿈은 Enter, 탭은 Tab 키로 전송됩니다.\r\n채팅창에서는 Enter로 메시지가 전송될 수 있습니다.\r\nDOCX의 서식·그림·머리글은 포함하지 않습니다.\r\n\r\n도움말을 열면 진행 중인 입력은 일시정지됩니다.\r\n도움말을 닫은 뒤 F10으로 이어 입력하세요." };
            dialog.Padding = new Padding(18);
            dialog.Controls.Add(text);
            PaintControls(dialog);
            dialog.ShowDialog(this);
        });
        help.DropDownItems.Add("프로그램 정보", null, (_, _) =>
        {
            if (cancellation != null && !paused) TogglePause();
            MessageBox.Show(this, $"문서 자동 입력 v{UpdateService.CurrentVersion}\nC# / Windows Forms\nTXT·DOCX 키보드 직접 입력 도구", "프로그램 정보");
        });
        var settings = new ToolStripMenuItem("설정");
        settings.DropDownItems.AddRange([darkMode, wordWrap, alwaysOnTop, minimizeOnStart, fontSizeMenu, new ToolStripSeparator()]);
        foreach (int size in new[] { 10, 12, 14, 16, 18, 22 })
        {
            var item = new ToolStripMenuItem($"{size}pt") { Tag = size };
            item.Click += (_, _) => { SetEditorFont(size); SaveSettings(); };
            fontSizeMenu.DropDownItems.Add(item);
        }
        settings.DropDownItems.Add("기본 설정으로 복원", null, (_, _) =>
        {
            darkMode.Checked = wordWrap.Checked = false;
            alwaysOnTop.Checked = minimizeOnStart.Checked = false;
            SetEditorFont(12);
            delay.Value = 5; interval.Value = 30;
            ApplyTheme(); SaveSettings();
        });
        darkMode.CheckedChanged += (_, _) => { ApplyTheme(); SaveSettings(); };
        wordWrap.CheckedChanged += (_, _) => { preview.WordWrap = wordWrap.Checked; SaveSettings(); };
        alwaysOnTop.CheckedChanged += (_, _) => { TopMost = alwaysOnTop.Checked; SaveSettings(); };
        minimizeOnStart.CheckedChanged += (_, _) => SaveSettings();
        delay.ValueChanged += (_, _) => { SaveSettings(); UpdateEstimate(); };
        interval.ValueChanged += (_, _) => { SaveSettings(); UpdateEstimate(); };
        menu.Items.AddRange([help, settings]);
        SetEditorFont(12);
    }

    void SetEditorFont(int size)
    {
        editorFontSize = size;
        // Inherited WinForms fonts can be shared by unrelated controls.
        // Release only fonts this form explicitly created for the editor.
        if (preview.Font.Name == "맑은 고딕" && preview.Font.SizeInPoints == size)
        {
            foreach (ToolStripMenuItem item in fontSizeMenu.DropDownItems) item.Checked = (int)item.Tag! == size;
            return;
        }
        var replacement = new Font("맑은 고딕", size);
        preview.Font = replacement;
        // WinForms may retain an equal font instead of replacing it. Keep our
        // font instances alive until all controls have been destroyed.
        ownedEditorFonts.Add(replacement);
        foreach (ToolStripMenuItem item in fontSizeMenu.DropDownItems)
            item.Checked = (int)item.Tag! == size;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            estimateTimer.Dispose();
            lifetime.Cancel();
            foreach (var font in ownedEditorFonts) font.Dispose();
            ownedEditorFonts.Clear();
        }
    }

    bool loadingSettings;
    void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
            if (settings == null) return;
            darkMode.Checked = settings.DarkMode;
            wordWrap.Checked = settings.WordWrap;
            alwaysOnTop.Checked = settings.AlwaysOnTop;
            minimizeOnStart.Checked = settings.MinimizeOnStart;
            saveWorkEnabled = settings.SaveWork;
            checkUpdatesOnStart = settings.CheckUpdatesOnStart;
            SetEditorFont(new[] { 10, 12, 14, 16, 18, 22 }.Contains(settings.FontSize) ? settings.FontSize : 12);
            delay.Value = Math.Clamp(settings.Delay, delay.Minimum, delay.Maximum);
            interval.Value = Math.Clamp(settings.Interval, 1, 1000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { ErrorLog.Write(ex, "설정 불러오기"); status.Text = "설정을 읽지 못해 기본 설정으로 시작합니다."; }
        finally { loadingSettings = false; }
    }

    void SaveSettings()
    {
        if (loadingSettings) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new UserSettings {
                DarkMode = darkMode.Checked, WordWrap = wordWrap.Checked,
                AlwaysOnTop = alwaysOnTop.Checked, MinimizeOnStart = minimizeOnStart.Checked, FontSize = editorFontSize,
                SaveWork = saveWorkEnabled, CheckUpdatesOnStart = checkUpdatesOnStart,
                Delay = delay.Value, Interval = (int)interval.Value }));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ErrorLog.Write(ex, "설정 저장"); status.Text = "설정을 저장하지 못했습니다. 현재 실행에는 적용됩니다."; }
    }

    void ApplyTheme()
    {
        PaintControls(this);
        menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColors(darkMode.Checked));
        void PaintItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = BackColor; item.ForeColor = ForeColor;
                if (item is ToolStripDropDownItem drop) PaintItems(drop.DropDownItems);
            }
        }
        PaintItems(menu.Items);
        Invalidate(true);
    }

    void PaintControls(Control control)
    {
        bool dark = darkMode.Checked;
        control.BackColor = dark ? Color.FromArgb(32, 34, 39) : SystemColors.Control;
        control.ForeColor = dark ? Color.FromArgb(235, 237, 242) : SystemColors.ControlText;
        if (control is TextBoxBase or NumericUpDown)
            control.BackColor = dark ? Color.FromArgb(43, 46, 53) : SystemColors.Window;
        if (control is Button button)
        {
            button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
            button.UseVisualStyleBackColor = !dark;
            button.FlatAppearance.BorderColor = Color.FromArgb(95, 103, 117);
        }
        foreach (Control child in control.Controls) PaintControls(child);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool first = Native.RegisterHotKey(Handle, 1, 0x4000, 0x77);
        bool second = Native.RegisterHotKey(Handle, 2, 0x4000, 0x78);
        bool third = Native.RegisterHotKey(Handle, 4, 0x4000, 0x79);
        if (!first || !second || !third) status.Text = "단축키가 다른 앱에서 사용 중입니다. 화면 버튼을 사용하세요.";
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i);
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            int key = m.WParam.ToInt32();
            if (key == 1 && !modalOpen && Native.IsWindowEnabled(Handle)) _ = StartInput();
            else if (key == 4 && !modalOpen && Native.IsWindowEnabled(Handle)) TogglePause();
            else if (key is 2 or 3) cancellation?.Cancel();
        }
        base.WndProc(ref m);
    }

    void LoadDocument()
    {
        using var dialog = new OpenFileDialog { Filter = "문서|*.txt;*.docx" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try { preview.Text = ReadDocument(dialog.FileName); documentName = Path.GetFileName(dialog.FileName); Text = "문서 자동 입력 — " + documentName; SaveCheckpoint(true); }
        catch (Exception ex) { ReportError(ex, "파일 읽기 실패"); }
    }

    internal static string ReadDocument(string path)
    {
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff)))
                return File.ReadAllText(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(949).GetString(bytes); }
        }
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX 본문을 찾을 수 없습니다.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        string Paragraph(XElement p) => string.Concat(p.Descendants().Select(n => n.Name == w + "t" ? n.Value : n.Name == w + "tab" ? "\t" : n.Name == w + "br" || n.Name == w + "cr" ? "\n" : ""));
        var body = doc.Root?.Element(w + "body") ?? throw new InvalidDataException("본문이 없습니다.");
        return string.Join("\r\n", body.Elements().SelectMany(block =>
            block.Name == w + "p" ? new[] { Paragraph(block) } :
            block.Name == w + "tbl" ? block.Elements(w + "tr").Select(row => string.Join("\t", row.Elements(w + "tc").Select(cell => string.Join("\n", cell.Elements(w + "p").Select(Paragraph))))) : Array.Empty<string>()));
    }

    void TogglePause()
    {
        if (cancellation == null || cancellation.IsCancellationRequested || closing) return;
        paused = !paused;
        pause.Text = paused ? "이어 입력 (F10)" : "일시정지 (F10)";
        if (paused) status.Text = "일시정지됨 · F10으로 같은 위치에서 이어 입력";
        else
        {
            resumeAt = Environment.TickCount64 + delay.Value * 1000;
            status.Text = "재개 대기 중 · 대상 입력창을 클릭하세요.";
        }
        SaveCheckpoint(true);
    }

    async Task WaitUntilReady(CancellationToken token)
    {
        while (paused || Environment.TickCount64 < resumeAt)
        {
            token.ThrowIfCancellationRequested();
            if (!paused)
                status.Text = $"{(resumeAt - Environment.TickCount64 + 999) / 1000}초 뒤 재개 · 대상 입력창을 클릭하세요.";
            await Task.Delay(50, token);
        }
        token.ThrowIfCancellationRequested();
    }

    void UpdateEstimate()
    {
        decimal seconds;
        if (cancellation == null)
        {
            int length = preview.Text.Replace("\r\n", "\n").Replace('\r', '\n').EnumerateRunes().Count();
            if (length == 0) { estimate.Text = "예상 완료: 내용을 불러오면 표시됩니다."; return; }
            int remaining = SavedWork.Normalize(preview.Text)[Math.Clamp(checkpointOffset, 0, SavedWork.Normalize(preview.Text).Length)..].EnumerateRunes().Count();
            seconds = delay.Value + remaining * interval.Value / 1000;
        }
        else
        {
            seconds = startupRemaining + (decimal)remainingCharacters * activeGap / 1000;
            if (paused) { estimate.Text = $"일시정지 · 남은 입력 약 {FormatDuration(seconds)} · 완료 시각은 재개 후 계산"; return; }
            seconds += Math.Max(0, (resumeAt - Environment.TickCount64) / 1000);
        }
        var now = DateTime.Now;
        string finish = seconds < (decimal)(DateTime.MaxValue - now).TotalSeconds - 1
            ? now.AddSeconds((double)seconds).ToString("yyyy-MM-dd HH:mm:ss") : "날짜 표시 범위 초과";
        estimate.Text = $"예상 완료: {finish} · 남은 시간 약 {FormatDuration(seconds)}";
    }

    static string FormatDuration(decimal seconds)
    {
        seconds = decimal.Ceiling(seconds);
        decimal days = decimal.Floor(seconds / 86400);
        return (days > 0 ? $"{days:N0}일 " : "") + $"{decimal.Floor(seconds % 86400 / 3600):00}:{decimal.Floor(seconds % 3600 / 60):00}:{seconds % 60:00}";
    }

    async Task StartInput(bool fromSaved = false)
    {
        if (cancellation != null || closing || modalOpen) return;
        string text = preview.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length == 0) { status.Text = "파일을 선택하거나 내용을 입력하세요."; return; }
        int offset = fromSaved ? new SavedWork(text, checkpointOffset, documentName).Validate().Offset : 0;
        if (offset >= text.Length) return;
        checkpointOffset = offset;
        inputSnapshot = text;
        using var cts = new CancellationTokenSource();
        cancellation = cts;
        paused = false;
        resumeAt = 0;
        pause.Enabled = true;
        start.Enabled = choose.Enabled = false;
        restore.Enabled = false;
        preview.ReadOnly = true;
        decimal wait = delay.Value;
        int gap = (int)interval.Value;
        startupRemaining = wait;
        remainingCharacters = text[offset..].EnumerateRunes().Count();
        activeGap = gap;
        saveWorkDirty = true;
        SaveCheckpoint(true);
        UpdateProgress();
        if (minimizeOnStart.Checked) WindowState = FormWindowState.Minimized;
        bool escape = Native.RegisterHotKey(Handle, 3, 0x4000, 0x1b);
        try
        {
            for (decimal sec = wait; sec > 0; sec--)
            {
                await WaitUntilReady(cts.Token);
                status.Text = $"{sec}초 뒤 시작 · 대상 입력창을 클릭하세요." + (escape ? "" : " 중지는 F9");
                await Task.Delay(1000, cts.Token);
                startupRemaining = sec - 1;
            }
            int count = offset;
            foreach (var rune in text[offset..].EnumerateRunes())
            {
                await WaitUntilReady(cts.Token);
                SendCharacter(rune.ToString());
                remainingCharacters--;
                count += rune.Utf16SequenceLength;
                checkpointOffset = count;
                saveWorkDirty = true;
                if (count % 10 == 0) status.Text = $"입력 중: {count:N0} / {text.Length:N0}";
                await Task.Delay(gap, cts.Token);
            }
            status.Text = "입력 전송 완료 · 대상 창에서 결과를 확인하세요.";
        }
        catch (OperationCanceledException) { if (!closing) status.Text = "중지됨 · 저장 위치에서 시작하면 이어 입력합니다. F8은 처음부터 시작합니다."; }
        catch (Exception ex) { ErrorLog.Write(ex, "입력 실패"); if (!closing) ReportError(ex, "입력 실패"); }
        finally
        {
            SaveCheckpoint(true);
            if (!closing)
            {
                Native.UnregisterHotKey(Handle, 3);
                start.Enabled = choose.Enabled = true;
                pause.Enabled = false;
                pause.Text = "일시정지 (F10)";
                preview.ReadOnly = false;
            }
            paused = false;
            resumeAt = 0;
            cancellation = null;
            if (!closing) { UpdateEstimate(); UpdateProgress(); }
        }
    }
}

sealed class UserSettings
{
    public bool DarkMode { get; set; }
    public bool WordWrap { get; set; }
    public decimal Delay { get; set; } = 5;
    public int Interval { get; set; } = 30;
    public bool AlwaysOnTop { get; set; }
    public bool MinimizeOnStart { get; set; }
    public int FontSize { get; set; } = 12;
    public bool SaveWork { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; }
}

sealed class ThemeColors(bool dark) : ProfessionalColorTable
{
    Color Background => dark ? Color.FromArgb(32, 34, 39) : SystemColors.Control;
    Color Highlight => dark ? Color.FromArgb(65, 79, 102) : SystemColors.Highlight;
    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuItemSelected => Highlight;
    public override Color MenuItemSelectedGradientBegin => Highlight;
    public override Color MenuItemSelectedGradientEnd => Highlight;
    public override Color MenuItemPressedGradientBegin => Background;
    public override Color MenuItemPressedGradientMiddle => Background;
    public override Color MenuItemPressedGradientEnd => Background;
    public override Color CheckBackground => Highlight;
    public override Color CheckSelectedBackground => Highlight;
    public override Color CheckPressedBackground => Highlight;
}

static class Native
{
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public KEYBOARD keyboard; [FieldOffset(0)] public MOUSE mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBOARD { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    internal static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            bool special = c is '\n' or '\t';
            ushort vk = special ? (ushort)(c == '\n' ? 13 : 9) : (ushort)0;
            uint flags = special ? 0u : 4u;
            foreach (uint state in new[] { flags, flags | 2u })
                inputs.Add(new INPUT { type = 1, data = new UNION { keyboard = new KEYBOARD { vk = vk, scan = special ? (ushort)0 : c, flags = state } } });
        }
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) != inputs.Count)
            throw new InvalidOperationException("키보드 입력을 전송하지 못했습니다. 대상 창의 실행 권한을 확인하세요.");
    }
}
