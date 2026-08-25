using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    public partial class MainForm : Form
    {
        private AppConfig _cfg;
        private Dictionary<string, CachedCred> _cache;

        private TreeView _tree;
        private Label _treeEmpty;
        private ModernTextBox _treeSearch;
        private ComboBox _profile;
        private CheckBox _noReboot;
        private ToolTip _tips;
        private ContextMenuStrip _nodeActionsMenu;
        private Label _pkgLabel;
        private TextBox _pkgBox;
        private Button _btnRun, _btnStop, _btnPreview;
        private const int PkgInstallIndex = 3;   // индексы режимов "пакеты" в _profile
        private const int PkgUpdateIndex = 4;
        private const int PkgRemoveIndex = 5;
        private const int PkgLockIndex = 6;      // versionlock: закрепить версию
        private const int PkgUnlockIndex = 7;    // versionlock: снять закрепление
        private const int PkgLockListIndex = 8;  // versionlock: показать закреплённые (только чтение)
        private Panel _leftPanel;
        private SplitContainer _workspaceSplit;
        private SplitContainer _contentSplit;
        private Button _btnToggleLog;
        private Panel _pageHost;
        private Panel _serversPage, _operationsPage, _fstecPage, _reportsPage, _accessPage, _settingsPage;
        private Label _selectionLabel;
        private readonly Dictionary<string, Button> _navigationButtons = new Dictionary<string, Button>();
        private readonly List<Control> _configurationControls = new List<Control>();
        private StatusChip _status;
        private Label _excluded;
        private ModernProgressBar _fstecProgress;
        private Label _fstecProgressLabel;
        private DataGridView _summary;
        private TextBox _log;
        private ModernTextBox _summarySearch;

        private CancellationTokenSource _cts;
        private volatile bool _running;
        private bool _closeAfterOperation;
        // Разрешение действует только до завершения текущей операции. Нужен для массового первого
        // подключения: оператор подтверждает один ключ и осознанно разрешает остальные новые ключи пакета.
        private volatile bool _trustUnknownHostKeysForOperation;
        private readonly Dictionary<string, DataGridViewRow> _rowByHost = new Dictionary<string, DataGridViewRow>();
        private readonly HashSet<string> _checkedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StringBuilder> _hostLogs = new Dictionary<string, StringBuilder>();
        private readonly object _logLock = new object();
        // Пишется из UI-потока (ShowHostLog/ShowAllLogs/ResetSummary), читается из фоновых SSH-потоков
        // внутри BufferLog - volatile гарантирует видимость изменения между потоками без явного lock
        // (сам объект - ссылка на string, присваивание которой уже атомарно; volatile здесь только
        // против переупорядочивания/кеширования, не про атомарность составных операций).
        private volatile string _selectedHost;
        private Label _logHint;
        private bool _lastLineProgress;   // последняя строка лога - прогресс reposync (следующую пишем на её место)
        private string _lastReportDir;    // папка последнего отчёта предпроверки

        public MainForm()
        {
            LoadConfigOrSeed();
            Theme.Configure(string.Equals(_cfg.UiTheme, "dark", StringComparison.OrdinalIgnoreCase));
            Text = "RED OS Package Updater";
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            Width = 1240; Height = 800; MinimumSize = new Size(980, 640); StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.UiFont;          // базовый шрифт наследуют все дочерние контролы
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            BuildUi();
            KeyDown += MainFormKeyDown;
            RebuildTree();
            RefreshExcluded();
            Shown += delegate { ApplyInitialWorkspaceLayout(); };
            Shown += async (s, e) => { await CheckAppUpdate(true); };
        }

        private void MainFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                if (_serversPage != null && _serversPage.Visible && _treeSearch != null) _treeSearch.Focus();
                else if (_summarySearch != null) _summarySearch.Focus();
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (_treeSearch != null && _treeSearch.Focused && _treeSearch.TextLength > 0) _treeSearch.Clear();
                else if (_summarySearch != null && _summarySearch.Focused && _summarySearch.TextLength > 0) _summarySearch.Clear();
                else return;
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            if (e.Alt && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D6)
            {
                string[] pages = { "servers", "operations", "fstec", "reports", "access", "settings" };
                ShowApplicationPage(pages[(int)e.KeyCode - (int)Keys.D1]);
                e.Handled = true; e.SuppressKeyPress = true;
            }
        }

        // ---------- Загрузка конфига / seed ----------
        private void LoadConfigOrSeed()
        {
            _cfg = Store.LoadConfig(null);
            bool empty = (_cfg.Systems.Count == 0 && _cfg.Credentials.Count == 0);
            if (!File.Exists(Store.ConfigPath) && empty)
            {
                string seed = Profiles.Read("seed_config.json");
                if (!string.IsNullOrEmpty(seed))
                {
                    var c = Store.FromJson(seed);
                    if (c != null) { _cfg = c; Store.SaveConfig(_cfg); }
                }
            }
            _cache = Store.LoadCache();
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            // Верхняя панель запуска
            var top = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = Theme.Surface, Padding = new Padding(12, 0, 12, 0) };
            Theme.EdgeLine(top, DockStyle.Bottom);
            // AutoSize=true у Label по умолчанию, но если после Text задать Width в том же
            // инициализаторе - явное значение переигрывает автоматически посчитанное, и текст
            // обрезается по этой (слишком узкой) ширине. Не задаём Width - пусть считает сам.
            var profileLbl = new Label { Text = "Сценарий", Left = 12, Top = 8, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.UiFontSmall };
            top.Controls.Add(profileLbl);
            var profileBox = new ModernCard { Left = 12, Top = 28, Width = 286, Height = 30, BackColor = Theme.Surface, CornerRadius = 6 };
            Theme.Box(profileBox);
            top.Controls.Add(profileBox);
            _profile = new ModernComboBox { Left = 2, Top = 2, Width = 282, Height = 26 };
            Theme.Combo(_profile);
            _profile.Items.Add("Ядро kernel-lt + security");
            _profile.Items.Add("Только security");
            _profile.Items.Add("Только ядро (kernel-lt)");
            _profile.Items.Add("Установить пакеты");     // индекс 3 = PkgInstallIndex
            _profile.Items.Add("Обновить пакеты");        // индекс 4 = PkgUpdateIndex
            _profile.Items.Add("Удалить пакеты");         // индекс 5 = PkgRemoveIndex
            _profile.Items.Add("Закрепить версию (versionlock)");   // индекс 6 = PkgLockIndex
            _profile.Items.Add("Снять закрепление версии");         // индекс 7 = PkgUnlockIndex
            _profile.Items.Add("Показать закреплённые версии");     // индекс 8 = PkgLockListIndex
            _profile.SelectedIndex = 0;
            _profile.SelectedIndexChanged += (s, e) => UpdateModeUi();
            profileBox.Controls.Add(_profile);
            _noReboot = new ModernCheckBox { Left = 12, Top = 64, Width = 170, Text = "Не перезагружать", BackColor = Theme.Surface };
            Theme.Check(_noReboot);
            top.Controls.Add(_noReboot);
            _tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
            _tips.SetToolTip(_noReboot,
                "Обновления ставятся как обычно, но если после них нужна перезагрузка - она не выполняется.\n" +
                "Узел останется в статусе \"нужен reboot\" (Warn) с новым ядром, но со старым запущенным.\n" +
                "Режим для предварительной установки вне окна обслуживания - перезагрузить все узлы можно\n" +
                "позже отдельным запуском (профиль \"Обновить пакеты\" + reboot, либо вручную).");
            // Поле пакетов (видно только в режимах "пакеты"), на второй строке вместо строки исключений.
            _pkgLabel = new Label { Left = 12, Top = 66, Width = 72, Height = 20, Text = "Пакеты:", Visible = false, ForeColor = Theme.Muted, Font = Theme.UiFontSmall, TextAlign = ContentAlignment.MiddleLeft };
            top.Controls.Add(_pkgLabel);
            _pkgBox = new ModernTextBox { Left = 88, Top = 61, Width = 400, Height = 28, Visible = false, Font = Theme.Mono, Placeholder = "package или package-version" };
            top.Controls.Add(_pkgBox);
            _btnPreview = new ModernButton { Width = 130, Height = 32, Text = "Предпроверка" };
            _btnPreview.Click += (s, e) => PreviewChecked();
            Theme.Secondary(_btnPreview);
            top.Controls.Add(_btnPreview);
            _btnRun = new ModernButton { Width = 174, Height = 32, Text = "Запустить отмеченные" };
            _btnRun.Click += (s, e) => RunChecked();
            Theme.Primary(_btnRun);
            top.Controls.Add(_btnRun);
            _btnStop = new ModernButton { Width = 72, Height = 32, Text = "Стоп", Enabled = false };
            _btnStop.Click += (s, e) => { if (_cts != null) _cts.Cancel(); SetStatus("Останавливаю..."); };
            Theme.Danger_(_btnStop);
            top.Controls.Add(_btnStop);
            _status = new StatusChip { Width = 236, Height = 28 };
            _status.SetStatus("Готово", StatusChip.Kind.Idle);
            top.Controls.Add(_status);
            _excluded = new Label { Left = 190, Top = 65, Width = 900, Height = 18, ForeColor = Theme.Danger, Cursor = Cursors.Hand, Text = "", AutoEllipsis = true };
            _excluded.Click += (s, e) => EditExclusions();
            top.Controls.Add(_excluded);
            _fstecProgress = new ModernProgressBar { Left = 12, Top = 69, Width = 880, Height = 8, Minimum = 0, Maximum = 100, Visible = false };
            _fstecProgressLabel = new Label { Left = 900, Top = 63, Width = 262, Height = 20, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted, Visible = false };
            top.Controls.Add(_fstecProgress);
            top.Controls.Add(_fstecProgressLabel);
            top.Resize += delegate { LayoutCommandBar(top, profileBox); };
            LayoutCommandBar(top, profileBox);

            // Левая панель: дерево + управление. Фон чуть темнее контента - отделяет навигацию от данных,
            // дерево внутри остаётся белой "карточкой" на этом фоне.
            // Width пошире, чем кажется нужным - у TreeView нет ellipsis/переноса: если текст узла
            // (имя системы + "[N]") не помещается по ширине, он просто обрезается по границе
            // контрола без "..." - это и была причина потерянной "]" на реальном Windows (там
            // Segoe UI шире, чем шрифт-заместитель в песочнице, где вёрстка проверялась).
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SidebarBg, Padding = new Padding(8, 6, 8, 8) };
            Theme.EdgeLine(left, DockStyle.Right);
            var treeHeader = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Theme.SidebarBg };
            var treeActions = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.SidebarBg };
            var treeTitle = Theme.SectionLabel("Серверы");
            treeTitle.Left = 2; treeTitle.Top = 5; treeTitle.Width = 150; treeTitle.Height = 20;
            var markNone = Theme.ToolbarButton("Снять", 56);
            var markAll = Theme.ToolbarButton("Отметить все", 94);
            markNone.Dock = DockStyle.Right; markAll.Dock = DockStyle.Right;
            markAll.Click += (s, e) => CheckAll(true);
            markNone.Click += (s, e) => CheckAll(false);
            _tips.SetToolTip(markAll, "Отметить все серверы");
            _tips.SetToolTip(markNone, "Снять все отметки");
            treeActions.Controls.Add(treeTitle);
            treeActions.Controls.Add(markAll);
            treeActions.Controls.Add(markNone);
            _treeSearch = new ModernTextBox { Dock = DockStyle.Bottom, Height = 28, Placeholder = "Поиск серверов..." };
            _treeSearch.TextChanged += delegate { RebuildTree(); };
            _tips.SetToolTip(_treeSearch, "Поиск по системе, имени, адресу и роли · Ctrl+F · Esc для очистки");
            treeHeader.Controls.Add(_treeSearch);
            treeHeader.Controls.Add(treeActions);
            _tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
            Theme.Tree(_tree);
            _tree.AfterCheck += TreeAfterCheck;
            _tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) { _tree.SelectedNode = e.Node; ShowTreeMenu(e.Node); } };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 7, 0, 0), BackColor = Theme.SidebarBg };
            Theme.EdgeLine(leftButtons, DockStyle.Top);
            AddCompactBtn(leftButtons, "+ Система", 82, () => AddSystem());
            AddCompactBtn(leftButtons, "+ Узел", 68, () => AddNode());
            AddCompactBtn(leftButtons, "Массово", 76, () => BulkNodes());
            _nodeActionsMenu = new ContextMenuStrip();
            Theme.ContextMenu(_nodeActionsMenu);
            _nodeActionsMenu.Items.Add("Изменить", null, (s, e) => EditSelected());
            _nodeActionsMenu.Items.Add("Сервисы системы", null, (s, e) => EditServices());
            _nodeActionsMenu.Items.Add(new ToolStripSeparator());
            _nodeActionsMenu.Items.Add("Удалить", null, (s, e) => DeleteSelected());
            Button more = null;
            more = AddCompactBtn(leftButtons, "Ещё ▾", 62, delegate { _nodeActionsMenu.Show(more, new Point(0, more.Height)); });
            var treeHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            _treeEmpty = new Label { Dock = DockStyle.Fill, Text = "Серверов пока нет\r\n\r\nДобавьте систему и первый узел", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Surface, Font = Theme.UiFontBodyLarge, Visible = false };
            treeHost.Controls.Add(_tree); treeHost.Controls.Add(_treeEmpty);
            left.Controls.Add(treeHost);
            left.Controls.Add(treeHeader);
            left.Controls.Add(leftButtons);
            _leftPanel = left;

            // Рабочая область: ширину панели серверов можно менять, но по умолчанию она компактна.
            _workspaceSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 348, BackColor = Theme.Bg, SplitterWidth = 5, FixedPanel = FixedPanel.Panel1 };
            _workspaceSplit.Panel1.Controls.Add(left);

            // Центр: сводка + журнал. Журнал можно скрыть, когда важнее видеть больше результатов.
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 410, BackColor = Theme.Bg, SplitterWidth = 5 };
            _contentSplit = split;
            split.Panel1.Padding = new Padding(8, 8, 8, 4);
            split.Panel2.Padding = new Padding(8, 4, 8, 8);
            _summary = new ModernDataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
            };
            Theme.Grid(_summary);
            var gridHeader = Theme.SectionLabel("Очередь и результаты");
            gridHeader.Dock = DockStyle.Top; gridHeader.Height = 26; gridHeader.Padding = new Padding(2, 0, 0, 5);
            _btnToggleLog = Theme.ToolbarButton("Скрыть журнал", 116);
            _btnToggleLog.Dock = DockStyle.Right;
            _btnToggleLog.Click += (s, e) => ToggleLogPanel();
            gridHeader.Controls.Add(_btnToggleLog);
            _summarySearch = new ModernTextBox { Dock = DockStyle.Right, Width = 226, Height = 26, Placeholder = "Поиск по результатам...", Margin = new Padding(0, 0, 8, 0) };
            _summarySearch.TextChanged += delegate { FilterSummaryRows(); };
            _tips.SetToolTip(_summarySearch, "Поиск по всем колонкам результатов · Ctrl+F · Esc для очистки");
            gridHeader.Controls.Add(_summarySearch); _summarySearch.BringToFront();
            AddCol(Col.System, "Система", 128); AddCol(Col.Name, "Узел", 150); AddCol(Col.Host, "IP / host", 112);
            AddCol(Col.St, "Статус", 92); AddCol(Col.Upd, "Результат", 110); AddCol(Col.Reb, "Reboot", 82);
            AddCol(Col.Pre, "Prestop", 70); AddCol(Col.Post, "Postcheck", 80); AddCol(Col.Ker, "Ядро", 150);
            AddCol(Col.Os, "ОС узла", 170);   // из /etc/os-release узла (маркер OS_INFO) - парк может быть смешанным
            AddCol(Col.Note, "Примечание", 260);
            _summary.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var lf = _summary.Rows[e.RowIndex].Tag as string;
                if (!string.IsNullOrEmpty(lf) && File.Exists(lf)) OpenPath(lf);
            };
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = Theme.Mono, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, HideSelection = false };
            _log.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.A) { _log.SelectAll(); e.Handled = true; e.SuppressKeyPress = true; } };
            var logBar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.Bg };
            var btnAllLogs = new ModernButton { Text = "Все узлы", Left = 0, Top = 3, Width = 90, Height = 28 };
            Theme.Secondary(btnAllLogs);
            btnAllLogs.Click += (s, e) => ShowAllLogs();
            var btnReports = new ModernButton { Text = "Папка отчётов", Left = 96, Top = 3, Width = 118, Height = 28 };
            Theme.Secondary(btnReports);
            btnReports.Click += (s, e) => OpenReportsFolder();
            _logHint = new Label { Left = 224, Top = 9, Width = 520, Text = "Выберите строку результата, чтобы видеть журнал только этого узла.", ForeColor = Theme.Muted };
            logBar.Controls.Add(btnAllLogs); logBar.Controls.Add(btnReports); logBar.Controls.Add(_logHint);
            _summary.SelectionChanged += (s, e) =>
            {
                if (_summary.CurrentRow == null) return;
                string host = Convert.ToString(_summary.CurrentRow.Cells[Col.Host].Value);
                if (!string.IsNullOrEmpty(host)) ShowHostLog(host);
            };
            split.Panel1.Controls.Add(_summary);
            split.Panel1.Controls.Add(gridHeader);
            split.Panel2.Controls.Add(_log);
            split.Panel2.Controls.Add(logBar);

            BuildApplicationShell(top, left, split);

            // иконка окна из вшитого ресурса
            try
            {
                using (var si = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (si != null) Icon = new System.Drawing.Icon(si);
            }
            catch { }

            UpdateModeUi();   // начальная видимость поля пакетов / строки исключений
        }

        // Режим "пакеты" (Установить/Обновить пакеты) выбран в списке профиля.
        private bool IsPkgMode() { return _profile.SelectedIndex >= PkgInstallIndex; }
        private string PkgAction()
        {
            switch (_profile.SelectedIndex)
            {
                case PkgInstallIndex: return "install";
                case PkgRemoveIndex: return "remove";
                case PkgLockIndex: return "lock";
                case PkgUnlockIndex: return "unlock";
                case PkgLockListIndex: return "locklist";
                default: return "update";
            }
        }
        // человекочитаемое имя действия для заголовков/подтверждений
        private static string ActionRu(string action)
        {
            switch (action)
            {
                case "install": return "Установка пакетов";
                case "remove": return "Удаление пакетов";
                case "lock": return "Закрепление версий";
                case "unlock": return "Снятие закрепления версий";
                case "locklist": return "Просмотр закреплённых версий";
                default: return "Обновление пакетов";
            }
        }

        private void UpdateModeUi()
        {
            bool pkg = IsPkgMode();
            if (_pkgLabel != null)
            {
                _pkgLabel.Visible = pkg;
                // для просмотра блокировок поле пакетов - необязательный фильтр (пусто = все)
                _pkgLabel.Text = (_profile.SelectedIndex == PkgLockListIndex) ? "Фильтр:" : "Пакеты:";
            }
            if (_pkgBox != null) _pkgBox.Visible = pkg;
            if (_noReboot != null) _noReboot.Visible = !pkg;   // для пакетов reboot не делаем
            if (_excluded != null) _excluded.Visible = !pkg;   // исключения к явной установке не относятся
        }

        // Список пакетов из поля (через пробел/строки), пусто -> null.
        private string PkgListFromBox()
        {
            var list = new List<string>();
            foreach (var tok in (_pkgBox.Text ?? "").Replace("\r", " ").Replace("\n", " ").Split(' '))
            { var t = tok.Trim(); if (t.Length > 0) list.Add(t); }
            return list.Count == 0 ? null : string.Join(" ", list.ToArray());
        }

        private Button AddBtn(Control parent, string text, Action act) { return AddBtn(parent, text, act, null); }
        // style==null - обычная второстепенная кнопка; иначе - явный стиль (акцент/опасное действие)
        private Button AddBtn(Control parent, string text, Action act, Action<Button> style)
        {
            var b = new ModernButton { Text = text, Width = 116, Height = 30, Margin = new Padding(3) };
            if (style != null) style(b); else Theme.Secondary(b);
            b.Click += (s, e) => { try { act(); } catch (Exception ex) { AppendLog("ОШИБКА: " + ex); AppDialog.Error(this, "Ошибка", ex.Message); } };
            parent.Controls.Add(b);
            return b;
        }

        private Button AddCompactBtn(Control parent, string text, int width, Action act)
        {
            var b = new ModernButton { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 5, 0) };
            Theme.Secondary(b);
            b.Click += (s, e) =>
            {
                try { if (act != null) act(); }
                catch (Exception ex) { AppendLog("ОШИБКА: " + ex); AppDialog.Error(this, "Ошибка", ex.Message); }
            };
            parent.Controls.Add(b);
            return b;
        }

        private void LayoutCommandBar(Panel top, Panel profileBox)
        {
            if (top == null || _btnPreview == null) return;
            CommandBarLayout layout = UiLayoutRules.CommandBar(top.ClientSize.Width, _status.Width);
            _btnPreview.Width = layout.PreviewWidth; _btnRun.Width = layout.RunWidth; _btnStop.Width = layout.StopWidth;
            _btnPreview.Left = layout.PreviewLeft; _btnRun.Left = layout.RunLeft; _btnStop.Left = layout.StopLeft;
            _status.Left = layout.StatusLeft; _status.Visible = !layout.Compact;
            _btnPreview.Top = _btnRun.Top = _btnStop.Top = 27;
            _status.Top = 29;

            // Вторая строка не конкурирует с действиями и остаётся полезной на минимальном окне.
            _pkgBox.Width = Math.Max(180, top.ClientSize.Width - _pkgBox.Left - 12);
            _excluded.Width = Math.Max(160, top.ClientSize.Width - _excluded.Left - 12);
            _fstecProgressLabel.Left = Math.Max(520, top.ClientSize.Width - 274);
            _fstecProgressLabel.Width = Math.Max(150, top.ClientSize.Width - _fstecProgressLabel.Left - 12);
            _fstecProgress.Width = Math.Max(300, _fstecProgressLabel.Left - 24);
        }

        private void ToggleLogPanel()
        {
            if (_contentSplit == null) return;
            bool show = _contentSplit.Panel2Collapsed;
            _contentSplit.Panel2Collapsed = !show;
            _btnToggleLog.Text = show ? "Скрыть журнал" : "Показать журнал";
        }

        private void ApplyInitialWorkspaceLayout()
        {
            LayoutServerWorkspace(_workspaceSplit);
            if (_contentSplit != null && _contentSplit.Height > 420)
            {
                _contentSplit.Panel1MinSize = 220;
                _contentSplit.Panel2MinSize = 120;
                int desired = (int)(_contentSplit.Height * 0.66);
                _contentSplit.SplitterDistance = Math.Min(desired, _contentSplit.Height - _contentSplit.Panel2MinSize - _contentSplit.SplitterWidth);
            }
        }

        private static void LayoutServerWorkspace(SplitContainer split)
        {
            if (split == null || split.ClientSize.Width < 500) return;
            int width = split.ClientSize.Width;
            ServerWorkspaceLayout layout = UiLayoutRules.ServerWorkspace(width, split.SplitterWidth);
            split.Panel1MinSize = 0; split.Panel2MinSize = 0;
            split.SplitterDistance = layout.SplitterDistance;
            split.Panel1MinSize = layout.LeftMinimum;
            split.Panel2MinSize = layout.RightMinimum;
        }
        private void AddCol(string name, string header, int w)
        {
            _summary.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = w, SortMode = DataGridViewColumnSortMode.Automatic });
        }

        private void FilterSummaryRows()
        {
            if (_summary == null || _running) return;
            string query = (_summarySearch == null ? "" : _summarySearch.Text).Trim();
            foreach (DataGridViewRow row in _summary.Rows)
            {
                bool visible = query.Length == 0;
                if (!visible)
                    foreach (DataGridViewCell cell in row.Cells)
                        if (Convert.ToString(cell.Value).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) { visible = true; break; }
                if (row != _summary.CurrentRow) row.Visible = visible;
            }
        }

        // Ключи колонок грида результатов. Раньше строковые литералы ("st","upd",...) были
        // продублированы в ~15 местах (AddCol + Cells[...] по всему файлу) - опечатка в одном из них
        // не ловится компилятором, а даёт ArgumentException в рантайме на реальной машине пользователя,
        // причём именно там, где это неудобнее всего диагностировать (посреди прогона обновления).
        private static class Col
        {
            public const string System = "system";
            public const string Name = "name";
            public const string Host = "host";
            public const string St = "st";
            public const string Upd = "upd";
            public const string Reb = "reb";
            public const string Pre = "pre";
            public const string Post = "post";
            public const string Ker = "ker";
            public const string Os = "os";
            public const string Note = "note";
        }

        // ---------- Дерево ----------
        private void RebuildTree()
        {
            string query = _treeSearch == null ? "" : (_treeSearch.Text ?? "").Trim();
            _suppressCheck = true;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            // Системы и узлы внутри них показываем по алфавиту, а не в порядке добавления в конфиг -
            // порядок добавления зависит от истории правок и выглядит произвольным. Сортируем копии
            // списков, сам _cfg.Systems/sys.Nodes (и их порядок в config.json) не трогаем.
            var systems = new List<SubSystem>(_cfg.Systems);
            systems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var sys in systems)
            {
                bool systemMatches = query.Length == 0 || (sys.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                var nodes = new List<Node>();
                foreach (Node candidate in sys.Nodes)
                    if (systemMatches || (candidate.Display ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (candidate.Role ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        nodes.Add(candidate);
                if (query.Length > 0 && !systemMatches && nodes.Count == 0) continue;
                // Имя - как обычно, счётчик в хвосте. Длинные имена не обрубаются посимвольно:
                // TreeView теперь рисует текст сам (Theme.Tree -> OwnerDrawText) с реальным
                // измерением шрифта и многоточием в конце, если не помещается.
                string tnText = sys.Name + "  [" + nodes.Count + (query.Length > 0 && nodes.Count != sys.Nodes.Count ? "/" + sys.Nodes.Count : "") + "]";
                var tn = new TreeNode(tnText) { Tag = sys, NodeFont = Theme.UiFontBold, ToolTipText = tnText };
                nodes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var n in nodes)
                {
                    var cn = new TreeNode(n.Display) { Tag = n, Checked = _checkedHosts.Contains(n.Host ?? ""), ToolTipText = n.Display };
                    if (!n.Enabled) cn.ForeColor = Theme.Disabled;
                    tn.Nodes.Add(cn);
                }
                tn.Checked = nodes.Count > 0 && nodes.TrueForAll(n => _checkedHosts.Contains(n.Host ?? ""));
                _tree.Nodes.Add(tn);
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            _suppressCheck = false;
            if (_treeEmpty != null)
            {
                _treeEmpty.Text = query.Length > 0 ? "Ничего не найдено\r\n\r\nИзмените поисковый запрос" : "Серверов пока нет\r\n\r\nДобавьте систему и первый узел";
                _treeEmpty.Visible = _tree.Nodes.Count == 0;
                if (_treeEmpty.Visible) _treeEmpty.BringToFront();
            }
            RefreshSelectionSummary();
        }

        private bool _suppressCheck;
        private void TreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressCheck) return;
            if (e.Node.Tag is SubSystem)
            {
                _suppressCheck = true;
                foreach (TreeNode c in e.Node.Nodes)
                {
                    c.Checked = e.Node.Checked;
                    var childNode = c.Tag as Node;
                    if (childNode != null)
                    {
                        if (e.Node.Checked) _checkedHosts.Add(childNode.Host ?? "");
                        else _checkedHosts.Remove(childNode.Host ?? "");
                    }
                }
                _suppressCheck = false;
            }
            else
            {
                var node = e.Node.Tag as Node;
                if (node != null)
                {
                    if (e.Node.Checked) _checkedHosts.Add(node.Host ?? "");
                    else _checkedHosts.Remove(node.Host ?? "");
                }
            }
            RefreshSelectionSummary();
        }

        private void ShowTreeMenu(TreeNode node)
        {
            var m = new ContextMenuStrip();
            Theme.ContextMenu(m);
            m.Closed += delegate
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(m.Dispose));
                else m.Dispose();
            };
            if (node.Tag is Node)
            {
                m.Items.Add("Предпроверка этого узла", null, (s, e) => RunPreviewTargets(CollectSingle(node)));
                m.Items.Add("Запустить этот узел", null, (s, e) => RunTargets(CollectSingle(node)));
                m.Items.Add("Изменить", null, (s, e) => EditSelected());
                m.Items.Add("Удалить", null, (s, e) => DeleteSelected());
            }
            else if (node.Tag is SubSystem)
            {
                m.Items.Add("Предпроверка системы", null, (s, e) => RunPreviewTargets(CollectSystem(node)));
                m.Items.Add("Запустить всю систему", null, (s, e) => RunTargets(CollectSystem(node)));
                m.Items.Add("Добавить узел", null, (s, e) => { _tree.SelectedNode = node; AddNode(); });
                m.Items.Add("Массовый ввод узлов", null, (s, e) => { _tree.SelectedNode = node; BulkNodes(); });
                m.Items.Add("Сервисы системы", null, (s, e) => { _tree.SelectedNode = node; EditServices(); });
                m.Items.Add("Переименовать", null, (s, e) => RenameSystem(node));
                m.Items.Add("Удалить систему", null, (s, e) => DeleteSelected());
            }
            m.Show(_tree, _tree.PointToClient(Cursor.Position));
        }

        private SubSystem CurrentSystem()
        {
            var n = _tree.SelectedNode;
            if (n == null) return null;
            if (n.Tag is SubSystem) return (SubSystem)n.Tag;
            if (n.Parent != null && n.Parent.Tag is SubSystem) return (SubSystem)n.Parent.Tag;
            return null;
        }

        // ---------- Управление узлами/системами ----------
        private void AddSystem()
        {
            if (!CanEditConfiguration()) return;
            string name = Prompt.Show("Новая система", "Название подсистемы:", "", false, new Size(360, 130));
            if (string.IsNullOrEmpty(name)) return;
            _cfg.Systems.Add(new SubSystem { Name = name.Trim() });
            Store.SaveConfig(_cfg); RebuildTree();
        }
        private void RenameSystem(TreeNode node)
        {
            var sys = node.Tag as SubSystem; if (sys == null) return;
            string name = Prompt.Show("Переименовать", "Название:", sys.Name, false, new Size(360, 130));
            if (string.IsNullOrEmpty(name)) return;
            sys.Name = name.Trim(); Store.SaveConfig(_cfg); RebuildTree();
        }
        private void AddNode()
        {
            if (!CanEditConfiguration()) return;
            var sys = CurrentSystem();
            if (sys == null) { AppDialog.Info(this, "Серверы", "Сначала выберите систему."); return; }
            using (var f = new NodeForm(null))
                if (f.ShowDialog(this) == DialogResult.OK) { sys.Nodes.Add(f.Result); Store.SaveConfig(_cfg); RebuildTree(); }
        }
        private void BulkNodes()
        {
            if (!CanEditConfiguration()) return;
            var sys = CurrentSystem();
            if (sys == null) { AppDialog.Info(this, "Серверы", "Сначала выберите систему."); return; }
            using (var f = new BulkNodesForm())
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null && f.Result.Count > 0)
                {
                    sys.Nodes.AddRange(f.Result);
                    Store.SaveConfig(_cfg); RebuildTree();
                    SetStatus("Добавлено узлов: " + f.Result.Count);
                }
        }
        private void EditSelected()
        {
            if (!CanEditConfiguration()) return;
            var n = _tree.SelectedNode; if (n == null) return;
            if (n.Tag is Node)
            {
                var node = (Node)n.Tag;
                using (var f = new NodeForm(node))
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        node.Name = f.Result.Name; node.Host = f.Result.Host; node.Port = f.Result.Port;
                        node.Role = f.Result.Role; node.Enabled = f.Result.Enabled;
                        Store.SaveConfig(_cfg); RebuildTree();
                    }
            }
            else if (n.Tag is SubSystem) RenameSystem(n);
        }
        private void DeleteSelected()
        {
            if (!CanEditConfiguration()) return;
            var n = _tree.SelectedNode; if (n == null) return;
            if (n.Tag is Node)
            {
                var sys = CurrentSystem();
                if (sys != null && AppDialog.Confirm(this, "Удаление узла", "Удалить узел " + ((Node)n.Tag).Host + "?", "Удалить"))
                { sys.Nodes.Remove((Node)n.Tag); Store.SaveConfig(_cfg); RebuildTree(); }
            }
            else if (n.Tag is SubSystem)
            {
                if (AppDialog.Confirm(this, "Удаление системы", "Удалить систему «" + ((SubSystem)n.Tag).Name + "» со всеми узлами?", "Удалить"))
                { _cfg.Systems.Remove((SubSystem)n.Tag); Store.SaveConfig(_cfg); RebuildTree(); }
            }
        }
        private void EditServices()
        {
            if (!CanEditConfiguration()) return;
            var sys = CurrentSystem();
            if (sys == null) { AppDialog.Info(this, "Серверы", "Сначала выберите систему."); return; }
            string cur = string.Join(Environment.NewLine, sys.Services.ToArray());
            string txt = Prompt.Show("Сервисы для остановки перед reboot", "Маски сервисов (по строке), напр. postgresql* / patroni:", cur, true, new Size(420, 300));
            if (txt == null) return;
            sys.Services = new List<string>();
            foreach (var line in txt.Replace("\r", "").Split('\n'))
            { var t = line.Trim(); if (t.Length > 0) sys.Services.Add(t); }
            Store.SaveConfig(_cfg);
            SetStatus("Сервисы системы обновлены");
        }
        private void CheckAll(bool val)
        {
            _suppressCheck = true;
            foreach (TreeNode sys in _tree.Nodes)
            {
                sys.Checked = val;
                foreach (TreeNode n in sys.Nodes)
                {
                    n.Checked = val;
                    var node = n.Tag as Node;
                    if (node != null)
                    {
                        if (val) _checkedHosts.Add(node.Host ?? "");
                        else _checkedHosts.Remove(node.Host ?? "");
                    }
                }
            }
            _suppressCheck = false;
            RefreshSelectionSummary();
        }

        private void EditCredentials()
        {
            if (!CanEditConfiguration()) return;
            using (var f = new CredentialsForm(_cfg.Credentials))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                { _cfg.Credentials = f.Result; Store.SaveConfig(_cfg); SetStatus("Учёток в пуле: " + _cfg.Credentials.Count); }
        }
        private void EditSettings()
        {
            if (!CanEditConfiguration()) return;
            using (var f = new SettingsForm(_cfg.Settings))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                { _cfg.Settings = f.Result; Store.SaveConfig(_cfg); SetStatus("Настройки сохранены"); }
        }
        private void RefreshExcluded()
        {
            string m = (_cfg.ExcludePackages == null || _cfg.ExcludePackages.Count == 0)
                ? "(ничего)" : string.Join(", ", _cfg.ExcludePackages.ToArray());
            if (_excluded != null) _excluded.Text = "Исключено из обновления: " + m + "   (клик — изменить)";
        }
        private void EditExclusions()
        {
            if (!CanEditConfiguration()) return;
            string cur = string.Join(Environment.NewLine, (_cfg.ExcludePackages ?? new List<string>()).ToArray());
            string txt = Prompt.Show("Исключить из обновления", "Маски пакетов (по строке), напр. postgresql* / postgrespro* / pgpro*:", cur, true, new System.Drawing.Size(440, 300));
            if (txt == null) return;
            var list = new List<string>();
            foreach (var line in txt.Replace("\r", "").Split('\n')) { var t = line.Trim(); if (t.Length > 0) list.Add(t); }
            _cfg.ExcludePackages = list;
            Store.SaveConfig(_cfg); RefreshExcluded();
            SetStatus("Исключения обновлены");
        }

        // ---------- Экспорт/импорт ----------
        private void DoExport()
        {
            string path;
            using (var sfd = new SaveFileDialog { Filter = "RPU export (*.rpu)|*.rpu|Все файлы (*.*)|*.*", FileName = "servers.rpu" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                path = sfd.FileName;
            }
            string master = Prompt.Show("Экспорт", "Мастер-пароль для шифрования экспорта:", "", false, new Size(380, 130));
            if (string.IsNullOrEmpty(master)) { AppDialog.Info(this, "Экспорт", "Для защищённого экспорта нужен мастер-пароль."); return; }
            try { Store.ExportPortable(path, master, _cfg); SetStatus("Экспортировано: " + path); }
            catch (Exception ex) { AppDialog.Error(this, "Ошибка экспорта", ex.Message); }
        }
        private void DoImport()
        {
            if (!CanEditConfiguration()) return;
            string path;
            using (var ofd = new OpenFileDialog { Filter = "RPU export (*.rpu)|*.rpu|Все файлы (*.*)|*.*" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                path = ofd.FileName;
            }
            string master = Prompt.Show("Импорт", "Мастер-пароль экспорта:", "", false, new Size(380, 130));
            if (string.IsNullOrEmpty(master)) return;
            try
            {
                var cfg = Store.ImportPortable(path, master);
                var choice = AppDialog.ImportChoice(this);
                if (choice == DialogResult.Cancel) return;
                if (choice == DialogResult.Yes)
                    _cfg = cfg;
                else
                {
                    _cfg.Systems.AddRange(cfg.Systems);
                    _cfg.Credentials.AddRange(cfg.Credentials);
                    _cfg.Credentials = DedupCreds(_cfg.Credentials);   // без дублей логин+пароль
                }
                Store.SaveConfig(_cfg); RebuildTree(); RefreshExcluded(); SetStatus("Импорт выполнен");
            }
            catch (Exception ex) { AppDialog.Error(this, "Ошибка импорта", "Проверьте мастер-пароль и файл.\n" + ex.Message); }
        }

        // ---------- Сбор целей и запуск ----------
        private bool CanEditConfiguration()
        {
            if (!_running) return true;
            AppDialog.Info(this, "Операция выполняется", "Изменение конфигурации будет доступно после завершения текущей операции.");
            return false;
        }

        private List<RunTarget> CollectChecked()
        {
            var list = new List<RunTarget>();
            foreach (TreeNode sysNode in _tree.Nodes)
            {
                var sys = sysNode.Tag as SubSystem;
                foreach (TreeNode nn in sysNode.Nodes)
                    if (nn.Checked && nn.Tag is Node && ((Node)nn.Tag).Enabled)
                        list.Add(new RunTarget(sys, (Node)nn.Tag));
            }
            return list;
        }
        private List<RunTarget> CollectSystem(TreeNode sysNode)
        {
            var list = new List<RunTarget>();
            var sys = sysNode.Tag as SubSystem;
            if (sys == null) return list;   // защита от неожиданного Tag - раньше здесь была NRE на sys.Nodes
            foreach (var n in sys.Nodes) if (n.Enabled) list.Add(new RunTarget(sys, n));
            return list;
        }
        // Один узел из правого клика в дереве ("Предпроверка/Запустить этот узел"). Систему берём из
        // иерархии самого node (node.Parent), а не из _tree.SelectedNode - раньше метод молча полагался
        // на то, что вызывающий код ВСЕГДА успевает выставить _tree.SelectedNode=node перед вызовом
        // (так и есть сейчас, но это скрытая, легко ломающаяся при рефакторинге связка).
        private List<RunTarget> CollectSingle(TreeNode node)
        {
            var list = new List<RunTarget>();
            var n = node.Tag as Node;
            if (n == null) return list;
            // Отключённый узел (серый в дереве) не должен запускаться в обход - раньше правый клик
            // "Запустить этот узел" игнорировал n.Enabled, в отличие от CollectChecked/CollectSystem,
            // которые его учитывают. Молчаливое расхождение поведения между тремя точками входа.
            if (!n.Enabled) { AppDialog.Info(this, "Узел отключён", "Сначала включите узел в его свойствах."); return list; }
            var sys = (node.Parent != null) ? node.Parent.Tag as SubSystem : null;
            list.Add(new RunTarget(sys, n));
            return list;
        }

        // Убрать узлы с одинаковым Host: иначе один сервер обновится двумя параллельными yum (rpm-lock),
        // и в гриде строки с одним host затирают друг друга.
        private List<RunTarget> DedupeByHost(List<RunTarget> targets)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outp = new List<RunTarget>();
            int dups = 0;
            foreach (var t in targets)
            {
                if (seen.Add(t.Node.Host ?? "")) outp.Add(t);
                else dups++;
            }
            if (dups > 0)
                AppDialog.Info(this, "Повтор адресов", "Узлов с повторяющимся Host/IP: " + dups + ". Дубли пропущены, чтобы на одном сервере не запускались параллельные обновления.");
            return outp;
        }

        private void RunChecked()
        {
            var t = CollectChecked();
            if (t.Count == 0) { AppDialog.Info(this, "Нет выбранных серверов", "Отметьте серверы в дереве или выберите цель через контекстное меню."); return; }
            RunTargets(t);
        }

        private void PreviewChecked()
        {
            var t = CollectChecked();
            if (t.Count == 0) { AppDialog.Info(this, "Нет выбранных серверов", "Отметьте серверы в дереве или выберите цель через контекстное меню."); return; }
            RunPreviewTargets(t);
        }

        // ---------- Общий каркас операций ----------
        private bool Preflight(List<RunTarget> targets)
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return false; }
            if (_cfg.Credentials.Count == 0) { AppDialog.Info(this, "Нет учётных записей", "Добавьте учётную запись в разделе «Доступ и SSH»."); return false; }
            if (targets == null || targets.Count == 0)
            {
                // Вызывающий код из контекстного меню дерева ("Запустить всю систему" на системе без
                // включённых узлов) раньше полагался только на эту проверку без своего MessageBox -
                // пункт меню молча "не срабатывал", пользователь не понимал, что произошло.
                AppDialog.Info(this, "Нет доступных серверов", "Для запуска нет включённых серверов.");
                return false;
            }
            return true;
        }

        private string ExcludeMasks()
        {
            return (_cfg.ExcludePackages != null) ? string.Join(" ", _cfg.ExcludePackages.ToArray()) : "";
        }

        // Папка логов конкретного запуска: Store.LogsDir\<prefix><timestamp>. Раньше эта строка была
        // продублирована по месту в 4 разных методах (RunTargets/RunRepoTargets/RunPkgOpTargets/
        // RunPreviewTargets), каждый со своим Path.Combine и своим форматом даты.
        private const string LogDirTimeFormat = "yyyy-MM-dd_HHmmss_fff";
        private static string NewLogDir(string prefix)
        {
            return Path.Combine(Store.LogsDir, prefix + DateTime.Now.ToString(LogDirTimeFormat));
        }

        // "Готово. OK: N  WARN: N  FAIL: N" - одинаковый подсчёт и текст статуса после RunTargets и
        // RunPkgOpTargets (раньше дублировался дословно в обоих местах).
        private void ReportBatchStatus(List<HostResult> res)
        {
            int ok = 0, warn = 0, fail = 0;
            foreach (var r in res) { if (r.Status == HostStatus.Ok) ok++; else if (r.Status == HostStatus.Warn) warn++; else fail++; }
            SetStatus(string.Format("Готово. OK: {0}  WARN: {1}  FAIL: {2}", ok, warn, fail));
        }

        private string SelectedProfileResource()
        {
            switch (_profile.SelectedIndex)
            {
                case 1: return Profiles.SecurityOnly;
                case 2: return Profiles.KernelOnly;
                default: return Profiles.KernelSecurity;
            }
        }

        // Ключ профиля для скрипта предпроверки (чтобы dry-run считал ту же транзакцию, что и боевой прогон).
        private string SelectedProfileKey()
        {
            switch (_profile.SelectedIndex)
            {
                case 1: return "security_only";
                case 2: return "kernel_only";
                default: return "kernel_security";
            }
        }

        // Очистить лог/сводку и создать строки под цели.
        private void ResetSummary(List<RunTarget> targets, string logHint)
        {
            lock (_logLock) { _hostLogs.Clear(); }
            _log.Clear(); _summary.Rows.Clear(); _rowByHost.Clear();
            foreach (var t in targets)
            {
                string host = t.Node.Host ?? "";   // null-ключ уронил бы словарь
                // system,name,host,st,upd,reb,pre,post,ker,os,note - по одному значению на каждую колонку грида
                int idx = _summary.Rows.Add(t.System != null ? t.System.Name : "", t.Node.Name, host, "в очереди", "", "", "", "", "", "", "");
                _rowByHost[host] = _summary.Rows[idx];
            }
            _selectedHost = null;   // живой лог показывает все узлы, пока не кликнут строку
            // подсказку ставим последней: добавление строк триггерит SelectionChanged и перетирает её
            if (_logHint != null) _logHint.Text = logHint;
        }

        // filteredLog=true - в живой лог идут только важные строки; false - весь вывод (для reposync).
        private SshOrchestrator NewOrchestrator(bool filteredLog)
        {
            var orch = new SshOrchestrator(_cfg.Credentials, _cache);
            orch.OnUnknownHostKey = ConfirmUnknownHostKey;
            if (filteredLog) orch.OnLog = (host, line) => { if (Important(line)) BufferLog(host, line); };
            else orch.OnLog = (host, line) => BufferLog(host, line);
            return orch;
        }

        private bool ConfirmUnknownHostKey(string host, int port, string fingerprint)
        {
            if (_trustUnknownHostKeysForOperation) return true;
            bool accepted = false;
            Action ask = () =>
            {
                // Пока этот запрос ожидал UI-поток, другой параллельный узел уже мог получить общее разрешение.
                if (_trustUnknownHostKeysForOperation) { accepted = true; return; }
                string text = "SSH-ключ этого сервера ранее не был известен.\n\n" +
                    "Сервер: " + host + "\n" +
                    "Порт: " + port + "\n" +
                    "SHA-256: " + fingerprint + "\n\n" +
                    "Сверьте отпечаток с доверенным источником.";
                using (var dlg = new Form())
                {
                    dlg.Text = "Первое подключение к серверу";
                    dlg.Width = 590; dlg.Height = 300; dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.Font = Theme.UiFont; dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text;
                    dlg.FormBorderStyle = FormBorderStyle.FixedDialog; dlg.MaximizeBox = false; dlg.MinimizeBox = false;
                    var label = new Label { Left = 18, Top = 18, Width = 540, Height = 145, Text = text };
                    var all = new ModernCheckBox { Left = 18, Top = 170, Width = 540, Height = 38, BackColor = Theme.Surface,
                        Text = "Доверять всем остальным новым SSH-ключам только в этой операции" };
                    var yes = new ModernButton { Text = "Доверять и сохранить", Left = 272, Top = 220,
                        Width = 170, Height = 30, DialogResult = DialogResult.Yes };
                    var no = new ModernButton { Text = "Отмена", Left = 450, Top = 220,
                        Width = 108, Height = 30, DialogResult = DialogResult.No };
                    Theme.Check(all); Theme.Primary(yes); Theme.Secondary(no);
                    dlg.Controls.Add(label); dlg.Controls.Add(all); dlg.Controls.Add(yes); dlg.Controls.Add(no);
                    dlg.AcceptButton = yes; dlg.CancelButton = no;
                    accepted = dlg.ShowDialog(this) == DialogResult.Yes;
                    if (accepted && all.Checked) _trustUnknownHostKeysForOperation = true;
                }
            };
            if (InvokeRequired) Invoke(ask); else ask();
            return accepted;
        }

        private void ManageHostKeys()
        {
            var known = Store.LoadKnownHosts();
            using (var dlg = new Form())
            {
                dlg.Text = "Сохранённые SSH-ключи";
                dlg.Width = 760; dlg.Height = 440; dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Font = Theme.UiFont; dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text;

                var list = new ModernListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                    MultiSelect = true, HideSelection = false, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
                list.Columns.Add("Сервер", 220);
                list.Columns.Add("SHA-256 fingerprint", 490);
                foreach (var kv in known)
                {
                    var item = new ListViewItem(kv.Key);
                    item.SubItems.Add(kv.Value ?? "");
                    list.Items.Add(item);
                }

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
                var remove = new ModernButton { Text = "Удалить выбранные", Width = 160, Dock = DockStyle.Left };
                var close = new ModernButton { Text = "Закрыть", Width = 100, Dock = DockStyle.Right, DialogResult = DialogResult.OK };
                remove.Click += (s, e) =>
                {
                    if (list.SelectedItems.Count == 0) return;
                    if (!AppDialog.Confirm(dlg, "Удаление SSH-ключей",
                        "После удаления при следующем подключении потребуется подтвердить новый ключ. Продолжить?",
                        "Удалить")) return;
                    var selected = new List<ListViewItem>();
                    foreach (ListViewItem item in list.SelectedItems) selected.Add(item);
                    foreach (var item in selected) { known.Remove(item.Text); list.Items.Remove(item); }
                    Store.SaveKnownHosts(known);
                };
                bottom.Controls.Add(remove); bottom.Controls.Add(close);
                dlg.Controls.Add(list); dlg.Controls.Add(bottom);
                Theme.Dialog(dlg);
                dlg.AcceptButton = close;
                dlg.ShowDialog(this);
            }
        }

        private void WireHostCallbacks(SshOrchestrator orch)
        {
            orch.OnHostStart = r => Ui(() => UpdateRow(r, true));
            orch.OnHostDone = r => Ui(() => UpdateRow(r, false));
            orch.OnHostPhase = (host, phase) => Ui(() => SetRowPhase(host, phase));
        }

        // Запустить фоновую операцию: флаги/UI/статус + перехват ошибок + сброс по завершении.
        private void StartOperation(string status, Action<CancellationToken> body)
        {
            if (_cts != null) throw new InvalidOperationException("Предыдущая операция ещё не завершена");
            var source = new CancellationTokenSource();
            _cts = source;
            _trustUnknownHostKeysForOperation = false;
            _running = true; SetRunningUi(true);
            SetStatus(status);
            var token = source.Token;
            Task.Factory.StartNew(() =>
            {
                try { body(token); }
                catch (OperationCanceledException) { Ui(() => { AppendLog("Операция отменена пользователем"); SetStatus("Остановлено"); }); }
                catch (Exception ex) { Ui(() => { AppendLog("ОБЩАЯ ОШИБКА: " + ex); SetStatus("Операция завершилась ошибкой"); }); }
                finally
                {
                    Ui(() =>
                    {
                        _running = false;
                        SetRunningUi(false);
                        if (ReferenceEquals(_cts, source)) _cts = null;
                        source.Dispose();
                        if (_closeAfterOperation)
                        {
                            _closeAfterOperation = false;
                            Close();
                        }
                    });
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // ---------- Боевой прогон ----------
        private void RunTargets(List<RunTarget> targets)
        {
            if (IsPkgMode()) { RunPkgOpTargets(targets, false); return; }   // режим "пакеты"
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);

            var opt = new RunOptions
            {
                Settings = _cfg.Settings,
                NoReboot = _noReboot.Checked,
                UpdateScript = Profiles.Read(SelectedProfileResource()),
                PostScript = Profiles.Read(Profiles.PostCheck),
                PreStopScript = Profiles.Read(Profiles.PreStop),
                RunLogDir = NewLogDir("run_"),
                ExcludeMasks = ExcludeMasks()
            };

            string exclInfo = string.IsNullOrEmpty(opt.ExcludeMasks) ? "(ничего)" : opt.ExcludeMasks;
            if (!AppDialog.Confirm(this, "Подтверждение операции", "Запустить на " + targets.Count + " узлах?\nПрофиль: " + _profile.Text +
                "\nИсключено из обновления: " + exclInfo +
                (_noReboot.Checked ? "\nБез перезагрузки" : "\nС перезагрузкой при необходимости"), "Запустить")) return;

            Directory.CreateDirectory(opt.RunLogDir);   // создаём только после подтверждения - не плодим пустые папки при отмене

            ResetSummary(targets, "Лог: все узлы. Клик по строке сводки — только её лог.");
            var orch = NewOrchestrator(true);
            WireHostCallbacks(orch);

            StartOperation("Выполняется на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunBatch(targets, opt, token);
                res = OrderLikeTargets(res, targets, r => r.Host);   // порядок как в дереве
                Ui(() =>
                {
                    ReportBatchStatus(res);
                    WriteSummaryFile(opt.RunLogDir, res);
                });
            });
        }

        // ---------- Обновление репозитория ----------
        private void OpenRepo()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            string repoHost; List<string> repoScripts;
            using (var f = new RepoDialog(_cfg.RepoHost, _cfg.RepoScripts))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                repoHost = f.Host; repoScripts = f.Scripts;
            }
            _cfg.RepoHost = repoHost; _cfg.RepoScripts = repoScripts; Store.SaveConfig(_cfg);
            RunRepoTargets(repoHost, repoScripts);
        }

        private void RunRepoTargets(string host, List<string> scripts)
        {
            if (_cfg.Credentials.Count == 0) { AppDialog.Info(this, "Нет учётных записей", "Добавьте учётную запись в разделе «Доступ и SSH»."); return; }
            var node = new Node { Name = "repo (" + host + ")", Host = host, Port = 22, Enabled = true };
            var target = new RunTarget(new SubSystem { Name = "Репозиторий" }, node);
            var targets = new List<RunTarget> { target };

            string logDir = NewLogDir("repo_");

            if (!AppDialog.Confirm(this, "Обновление репозитория", "Запустить обновление репозитория на " + host + "?\nСкриптов: " + scripts.Count, "Запустить")) return;

            Directory.CreateDirectory(logDir);   // после подтверждения

            ResetSummary(targets, "Обновление репозитория. Весь вывод скрипта - в логе ниже.");
            var orch = NewOrchestrator(false);   // весь вывод reposync, без фильтра
            WireHostCallbacks(orch);
            orch.OnRepoProgress = (h, line) => BufferLog(h, line, true);       // прогресс - на месте
            orch.OnRepoCount = (h, done, total) => Ui(() => SetRepoCount(h, done, total));

            StartOperation("Обновление репозитория на " + host + "...", token =>
            {
                var r = orch.RunRepo(target, scripts, _cfg.Settings, logDir, token);
                Ui(() => SetStatus("Репозиторий: " + StatusText(r.Status) + " | " + r.Note));
            });
        }

        private void RunVulnerabilityScan()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            var targets = DedupeByHost(CollectChecked());
            if (targets.Count == 0) { AppDialog.Info(this, "Нет выбранных серверов", "Отметьте серверы для проверки."); return; }
            if (!Preflight(targets)) return;
            if (!AppDialog.Confirm(this, "Проверка уязвимостей ФСТЭК",
                "Проверить " + targets.Count + " узлов по security advisory RED OS и связать CVE с БДУ ФСТЭК?\n\n" +
                "Trivy и дополнительные пакеты на узлах не устанавливаются. Серверы не перезагружаются.", "Проверить")) return;

            string logDir = NewLogDir("vuln_");
            Directory.CreateDirectory(logDir);
            ResetSummary(targets, "Уязвимости ФСТЭК. Полный список — в логе каждого узла.");
            var orch = NewOrchestrator(true);
            WireHostCallbacks(orch);
            string script = Profiles.Read(Profiles.AdvisoryScan);

            StartOperation("Проверка ФСТЭК на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunPkgOp(targets, "vuln", "", true, script, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, r => r.Host);
                Ui(() => { ReportBatchStatus(res); WriteSummaryFile(logDir, res); WriteVulnerabilityReport(logDir, res); });
            });
        }

        private void WriteVulnerabilityReport(string logDir, List<HostResult> results)
        {
            try
            {
                VulnerabilityReportOutput output = VulnerabilityReportService.WriteCsv(logDir, results);
                string htmlPath = Path.Combine(logDir, "fstec_report.html");
                File.WriteAllText(htmlPath, BuildVulnerabilityHtml(results), new UTF8Encoding(false));
                _lastReportDir = logDir;
                AppendLog("Отчёт ФСТЭК: " + output.FstecCsvPath);
                AppendLog("Подтверждено для версии ОС: " + output.ConfirmedBduCount + "; исключено неподтверждённых/неприменимых совпадений: " + output.RejectedBduCount);
                AppendLog("Расширенный отчёт: " + output.AllCsvPath);
                AppendLog("HTML-отчёт: " + htmlPath);
                if (output.LinuxFindingsAdded > 0) AppendLog("Сопоставление advisory/общего Linux с БДУ: добавлено " + output.LinuxFindingsAdded + " подтверждённых записей");
            }
            catch (Exception ex) { AppendLog("Не удалось сформировать отчёт ФСТЭК: " + ex.Message); }
        }

        private static string BuildVulnerabilityHtml(List<HostResult> results)
        {
            int total = 0, critical = 0, high = 0, fixable = 0;
            foreach (var r in results)
                foreach (var v in r.Vulnerabilities ?? new List<VulnerabilityFinding>())
                    if (VulnerabilityReportService.IsConfirmedBdu(v))
                    {
                        total++;
                        if (string.Equals(v.Severity, "CRITICAL", StringComparison.OrdinalIgnoreCase)) critical++;
                        if (string.Equals(v.Severity, "HIGH", StringComparison.OrdinalIgnoreCase)) high++;
                        if (!string.IsNullOrWhiteSpace(v.FixedVersion)) fixable++;
                    }

            var remediationGroups = results
                .SelectMany(r => (r.Vulnerabilities ?? new List<VulnerabilityFinding>())
                    .Where(VulnerabilityReportService.IsConfirmedBdu)
                    .Select(v => new { Host = r, Finding = v }))
                .GroupBy(x => new {
                    Host = x.Host.Host ?? "", Name = x.Host.Name ?? "",
                    Package = x.Finding.Package ?? "", Installed = x.Finding.InstalledVersion ?? "",
                    Fixed = x.Finding.FixedVersion ?? ""
                })
                .Select(g => new {
                    Host = g.First().Host, Package = g.Key.Package, Installed = g.Key.Installed,
                    Fixed = g.Key.Fixed, Findings = g.Select(x => x.Finding).ToList(),
                    Severity = g.Select(x => x.Finding.Severity ?? "UNKNOWN")
                        .OrderByDescending(SeverityRank).FirstOrDefault() ?? "UNKNOWN"
                }).OrderBy(g => g.Host.Name).ThenBy(g => g.Package).ThenBy(g => g.Fixed).ToList();

            var h = new StringBuilder(1024 * 64);
            h.Append("<!doctype html><html lang='ru'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>")
             .Append("<title>Отчёт по уязвимостям ФСТЭК</title><style>")
             .Append(":root{color-scheme:light;--bg:#f4f6f9;--surface:#fff;--text:#1b2433;--muted:#657082;--line:#dadee7;--head:#f7f8fa;--accent:#265bcf;--tint:#ebf2ff;--bad:#c93a3a;--warn:#b57d0b;--good:#228b54}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px 'Segoe UI',Arial,sans-serif}header{background:var(--surface);border-top:4px solid var(--accent);padding:24px clamp(18px,4vw,48px);border-bottom:1px solid var(--line)}h1{font-size:24px;letter-spacing:-.3px;margin:0 0 6px}.muted{color:var(--muted)}.wrap{padding:22px clamp(14px,4vw,48px);max-width:1800px;margin:auto}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:18px}.card,.tools,table{background:var(--surface);border:1px solid var(--line);box-shadow:0 2px 8px rgba(27,36,51,.04)}.card{padding:15px 18px;min-width:150px;border-radius:9px}.num{font-size:25px;font-weight:650;letter-spacing:-.5px}.bad{color:var(--bad)}.warn{color:var(--warn)}.good{color:var(--good)}.tools{display:flex;gap:10px;flex-wrap:wrap;padding:12px;margin-bottom:12px;border-radius:9px;align-items:center}input,select{border:1px solid #bcc4d1;border-radius:7px;padding:9px 11px;background:#fff;color:var(--text);min-width:180px;outline:none}input:focus,select:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(38,91,207,.12)}table{width:100%;border-collapse:separate;border-spacing:0;font-size:12px;border-radius:9px;overflow:hidden}th{position:sticky;top:0;background:var(--head);color:var(--muted);text-align:left;padding:10px;border-bottom:1px solid var(--line);z-index:1}td{padding:9px 10px;border-bottom:1px solid #e8ebf0;vertical-align:top}tbody tr:last-child td{border-bottom:0}tr:hover{background:#f2f6ff}.sev-CRITICAL,.sev-HIGH{color:var(--bad);font-weight:650}.sev-MEDIUM{color:var(--warn)}.tag{display:inline-block;padding:2px 7px;border-radius:5px;background:var(--tint);color:#1d4ed8;white-space:nowrap}a{color:var(--accent);text-decoration:none;font-weight:600}a:hover{text-decoration:underline}.hidden{display:none}.hostsum{margin-bottom:18px}details summary{cursor:pointer;color:var(--accent);font-weight:650;user-select:none}.vulns{margin-top:9px;display:grid;gap:8px}.vuln{padding:8px 10px;border-left:3px solid var(--line);background:var(--head);border-radius:0 6px 6px 0}.vuln-head{display:flex;gap:8px;align-items:baseline;flex-wrap:wrap}.vuln-title{margin-top:3px;color:var(--text)}@media(max-width:760px){.wrap{padding:14px}.tools>*{width:100%}table{display:block;overflow-x:auto;white-space:nowrap}}</style></head><body>")
             .Append("<header><h1>Отчёт по уязвимостям БДУ ФСТЭК</h1><div class='muted'>Сформирован ").Append(H(DateTime.Now.ToString("dd.MM.yyyy HH:mm"))).Append("</div></header><div class='wrap'>")
             .Append("<div class='cards'><div class='card'><div class='num'>").Append(total).Append("</div><div class='muted'>записей БДУ</div></div>")
             .Append("<div class='card'><div class='num bad'>").Append(critical).Append("</div><div class='muted'>критических</div></div>")
             .Append("<div class='card'><div class='num warn'>").Append(high).Append("</div><div class='muted'>высоких</div></div>")
             .Append("<div class='card'><div class='num good'>").Append(remediationGroups.Count(g => !string.IsNullOrWhiteSpace(g.Fixed))).Append("</div><div class='muted'>действий обновления</div></div>")
             .Append("<div class='card'><div class='num'>").Append(remediationGroups.Count).Append("</div><div class='muted'>групп исправления</div></div>")
             .Append("<div class='card'><div class='num'>").Append(results.Count).Append("</div><div class='muted'>серверов</div></div></div>")
             .Append("<table class='hostsum'><thead><tr><th>Сервер</th><th>Узел</th><th>Статус</th><th>Результат</th></tr></thead><tbody>");
            foreach (var r in results)
                h.Append("<tr><td>").Append(H(NodeLabel(r.Name, r.Host))).Append("</td><td>").Append(H(r.Name)).Append("</td><td>").Append(H(StatusText(r.Status))).Append("</td><td>").Append(H(r.Note)).Append("</td></tr>");
            h.Append("</tbody></table><div class='tools'><input id='q' placeholder='Поиск по БДУ, CVE, пакету...'><select id='sev'><option value=''>Любая критичность</option><option>CRITICAL</option><option>HIGH</option><option>MEDIUM</option><option>LOW</option><option>UNKNOWN</option></select><select id='fix'><option value=''>Все записи</option><option value='yes'>Есть исправление</option><option value='no'>Нет исправления</option></select><select id='host'><option value=''>Все серверы</option>");
            var hosts = new List<string>();
            foreach (var r in results)
                if (!hosts.Contains(r.Host ?? ""))
                {
                    hosts.Add(r.Host ?? "");
                    h.Append("<option value='").Append(H(r.Host)).Append("'>").Append(H(NodeLabel(r.Name, r.Host))).Append("</option>");
                }
            h.Append("</select><span class='muted' id='shown'></span></div><table id='v'><thead><tr><th>Сервер</th><th>Пакет</th><th>Установлено</th><th>Обновить до</th><th>Макс. критичность</th><th>Закрываемые уязвимости</th></tr></thead><tbody>");
            foreach (var group in remediationGroups)
            {
                bool hasFix = !string.IsNullOrWhiteSpace(group.Fixed);
                h.Append("<tr data-sev='").Append(H(group.Severity)).Append("' data-fix='").Append(hasFix ? "yes" : "no").Append("' data-host='").Append(H(group.Host.Host)).Append("'><td>").Append(H(NodeLabel(group.Host.Name, group.Host.Host))).Append("</td><td><strong>").Append(H(group.Package)).Append("</strong></td><td>").Append(H(group.Installed)).Append("</td><td>").Append(hasFix ? "<span class='tag'>" + H(group.Fixed) + "</span>" : "—").Append("</td><td class='sev-").Append(H(group.Severity)).Append("'>").Append(H(group.Severity)).Append("</td><td><details><summary>").Append(group.Findings.Count).Append(" ").Append(group.Findings.Count == 1 ? "уязвимость" : "уязвимостей").Append("</summary><div class='vulns'>");
                foreach (var v in group.Findings.OrderByDescending(x => SeverityRank(x.Severity)).ThenBy(x => x.Id))
                {
                    var cves = VulnerabilityReportService.RelatedCves(v);
                    h.Append("<div class='vuln'><div class='vuln-head'><a href='").Append(H(VulnerabilityReportService.PrimaryUrl(v))).Append("'>").Append(H(v.Id)).Append("</a><span class='sev-").Append(H(v.Severity)).Append("'>").Append(H(v.Severity)).Append("</span><span class='muted'>").Append(H(v.PublishedDate)).Append("</span></div><div class='muted'>").Append(H(string.Join(", ", cves.ToArray()))).Append("</div><div class='vuln-title'>").Append(H(v.Title)).Append("</div></div>");
                }
                h.Append("</div></details></td></tr>");
            }
            h.Append("</tbody></table></div><script>const q=document.getElementById('q'),s=document.getElementById('sev'),f=document.getElementById('fix'),ho=document.getElementById('host'),rows=[...document.querySelectorAll('#v tbody tr')],shown=document.getElementById('shown');function run(){let n=0,Q=q.value.toLowerCase();rows.forEach(r=>{let ok=(!Q||r.innerText.toLowerCase().includes(Q))&&(!s.value||r.dataset.sev==s.value)&&(!f.value||r.dataset.fix==f.value)&&(!ho.value||r.dataset.host==ho.value);r.classList.toggle('hidden',!ok);if(ok)n++});shown.textContent='Показано групп: '+n} [q,s,f,ho].forEach(x=>x.addEventListener(x.tagName=='INPUT'?'input':'change',run));run();</script></body></html>");
            return h.ToString();
        }

        private static int SeverityRank(string severity)
        {
            switch ((severity ?? "").ToUpperInvariant())
            {
                case "CRITICAL": return 5;
                case "HIGH": return 4;
                case "MEDIUM": return 3;
                case "LOW": return 2;
                default: return 1;
            }
        }

        private static string H(string value)
        {
            return System.Net.WebUtility.HtmlEncode(value ?? "");
        }

        private static string NodeLabel(string name, string host)
        {
            return HostIdentity.Label(name, host);
        }

        private void UpdateVulnerabilityDb()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            ShowVulnerabilityDbProgress();
            StartOperation("Загрузка базы ФСТЭК...", token =>
            {
                try
                {
                    VulnerabilityDatabaseService.UpdateAll(progress =>
                    {
                        Ui(() => UpdateVulnerabilityDbProgress(progress.Percent,
                            progress.Done / (1024 * 1024), progress.Total > 0 ? progress.Total / (1024 * 1024) : 0,
                            progress.Stage == VulnerabilityDatabaseStage.TrivyDatabase ? "Дополнительная база Trivy" : "Каталог БДУ ФСТЭК"));
                    }, token);
                    Ui(() => { RefreshVulnerabilityDbStatus(); AppDialog.Info(this, "ФСТЭК", "Каталог БДУ ФСТЭК успешно обновлён."); });
                }
                catch (OperationCanceledException) { Ui(() => SetStatus("Загрузка базы ФСТЭК отменена")); }
                catch (Exception ex) { Ui(() => { SetStatus("Ошибка загрузки базы ФСТЭК"); AppDialog.Error(this, "ФСТЭК", "Не удалось обновить базу:\n" + ex.Message); }); }
                finally { Ui(HideVulnerabilityDbProgress); }
            });
        }

        private void ShowVulnerabilityDbProgress()
        {
            _excluded.Visible = false;
            _fstecProgress.Style = ProgressBarStyle.Marquee;
            _fstecProgress.Value = 0;
            _fstecProgress.Visible = true;
            _fstecProgressLabel.Text = "Подключение к RED SOFT...";
            _fstecProgressLabel.Visible = true;
        }

        private void UpdateVulnerabilityDbProgress(int percent, long doneMb, long totalMb, string stage)
        {
            if (percent >= 0)
            {
                _fstecProgress.Style = ProgressBarStyle.Continuous;
                _fstecProgress.Value = Math.Max(0, Math.Min(100, percent));
                _fstecProgressLabel.Text = stage + ": " + doneMb + " / " + totalMb + " МБ  (" + percent + "%)";
                SetStatus(stage + " " + percent + "%");
            }
            else
            {
                _fstecProgress.Style = ProgressBarStyle.Marquee;
                _fstecProgressLabel.Text = stage + ": " + doneMb + " МБ загружено";
            }
        }

        private void HideVulnerabilityDbProgress()
        {
            _fstecProgress.Visible = false;
            _fstecProgressLabel.Visible = false;
            _excluded.Visible = !_pkgBox.Visible;
        }

        private void ImportVulnerabilityDb()
        {
            if (_running) { AppDialog.Info(this, "Операция выполняется", "Дождитесь завершения текущей операции или остановите её."); return; }
            using (var d = new OpenFileDialog { Title = "Официальная XML-выгрузка или компактный каталог ФСТЭК", Filter = "Каталог ФСТЭК|*.zip|Дополнительная база Trivy|*.tar.gz;*.tgz|Все файлы|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VulnerabilityDatabaseService.Import(d.FileName, CancellationToken.None);
                    RefreshVulnerabilityDbStatus(); AppDialog.Info(this, "ФСТЭК", "База уязвимостей успешно импортирована.");
                }
                catch (Exception ex) { AppDialog.Error(this, "ФСТЭК", "Ошибка импорта:\n" + ex.Message); }
            }
        }

        private void RefreshVulnerabilityDbStatus()
        {
            string status = VulnerabilityDb.StatusText();
            SetStatus(status);
        }

        // ---------- Установка/обновление произвольных пакетов (режим "пакеты" в списке профиля) ----------
        // dryRun=true - предпроверка (ничего не ставим), false - боевой прогон.
        private void RunPkgOpTargets(List<RunTarget> targets, bool dryRun)
        {
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);
            string action = PkgAction();
            bool listOnly = action == "locklist";   // просмотр закреплённых версий - только чтение
            if (listOnly) dryRun = true;             // ничего не меняем, оба режима = показать список
            string packages = PkgListFromBox();
            if (packages == null)
            {
                if (!listOnly) { AppDialog.Info(this, "Не указаны пакеты", "Введите пакеты в поле сценария — через пробел или по одному на строку."); return; }
                packages = "";   // для просмотра список необязателен (пусто = все закреплённые)
            }
            string actRu = ActionRu(action);

            if (!dryRun)
            {
                string warn;
                if (action == "remove")
                    warn = "\n\nВНИМАНИЕ: удаление может потянуть за собой зависимые пакеты - сверьтесь с предпроверкой.";
                else if (action == "lock")
                    warn = "\n\nВерсии будут закреплены: dnf update перестанет обновлять эти пакеты.";
                else if (action == "unlock")
                    warn = "\n\nЗакрепление версий будет снято: пакеты снова начнут обновляться.";
                else
                    warn = "\n\nReboot не выполняется (только сообщение, если нужен).";
                if (!AppDialog.Confirm(this, "Подтверждение операции", actRu + " на " + targets.Count + " узлах:\n" + packages + warn + "\n\nВыполнить?",
                    action == "remove" ? "Удалить" : "Выполнить")) return;
            }

            string prefix = dryRun ? "pkgpreview_" : "pkgop_";
            string logDir = NewLogDir(prefix);
            Directory.CreateDirectory(logDir);
            string script = Profiles.Read(Profiles.PkgOp);

            string title = listOnly ? actRu : (dryRun ? "Предпроверка: " + actRu.ToLower() : actRu);
            ResetSummary(targets, title + ". Клик по строке — лог узла.");
            var orch = NewOrchestrator(true);
            WireHostCallbacks(orch);

            StartOperation(title + " на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunPkgOp(targets, action, packages, dryRun, script, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, r => r.Host);   // порядок как в дереве
                Ui(() =>
                {
                    ReportBatchStatus(res);
                    WriteSummaryFile(logDir, res);
                });
            });
        }

        // ---------- Предпроверка (dry-run) ----------
        private void RunPreviewTargets(List<RunTarget> targets)
        {
            if (IsPkgMode()) { RunPkgOpTargets(targets, true); return; }   // режим "пакеты" - dry-run
            if (!Preflight(targets)) return;
            targets = DedupeByHost(targets);

            string logDir = NewLogDir("preview_");
            Directory.CreateDirectory(logDir);
            string excl = ExcludeMasks();
            string previewScript = Profiles.Read(Profiles.Preview);
            string profileKey = SelectedProfileKey();

            ResetSummary(targets, "Предпроверка (реальная транзакция профиля). Клик по строке — лог узла.");
            var orch = NewOrchestrator(true);
            orch.OnPreviewStart = host => Ui(() => SetRowPhase(host, "preview"));
            orch.OnPreviewDone = hp => Ui(() => UpdatePreviewRow(hp));

            StartOperation("Предпроверка на " + targets.Count + " узлах...", token =>
            {
                var res = orch.RunPreview(targets, previewScript, excl, profileKey, _cfg.Settings, logDir, token);
                res = OrderLikeTargets(res, targets, h => h.Host);   // порядок как в дереве, не по завершению
                Ui(() =>
                {
                    int totW = 0, totS = 0, totD = 0; foreach (var h in res) { totW += h.Total; totS += h.Sec; totD += h.Dep; }
                    SetStatus(string.Format("Предпроверка готова: в транзакции {0} (advisory {1}, завис. {2})", totW, totS, totD));
                    if (res.Count > 0)
                    {
                        string html = null, xls = null;
                        try { html = PreviewReport.Build(res, logDir); AppendLog("Отчёт (HTML): " + html); }
                        catch (Exception ex) { AppendLog("Не удалось сформировать HTML: " + ex.Message); }
                        try { xls = PreviewReport.BuildXlsx(res, logDir); AppendLog("Отчёт (Excel): " + xls); }
                        catch (Exception ex) { AppendLog("Не удалось сформировать XLS: " + ex.Message); }
                        _lastReportDir = logDir;
                        if (Visible) { try { Activate(); } catch { } }
                        OfferOpenReport(html, xls);   // диалог с выбором: HTML / Excel / оба / папка
                    }
                });
            });
        }

        // Упорядочить результаты как в дереве (по порядку целей запуска), а не по порядку завершения.
        private static List<T> OrderLikeTargets<T>(List<T> res, List<RunTarget> targets, Func<T, string> hostOf)
        {
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++)
            {
                string h = targets[i].Node.Host ?? "";
                if (!pos.ContainsKey(h)) pos[h] = i;
            }
            var outl = new List<T>(res);
            outl.Sort((a, b) =>
            {
                int ia; if (!pos.TryGetValue(hostOf(a) ?? "", out ia)) ia = int.MaxValue;
                int ib; if (!pos.TryGetValue(hostOf(b) ?? "", out ib)) ib = int.MaxValue;
                return ia.CompareTo(ib);
            });
            return outl;
        }

        // Убрать дубли учёток по (логин+пароль).
        private static List<Credential> DedupCreds(List<Credential> list)
        {
            var seen = new HashSet<string>();
            var outp = new List<Credential>();
            foreach (var c in list)
                if (c != null && seen.Add((c.User ?? "") + "\0" + (c.Password ?? ""))) outp.Add(c);
            return outp;
        }

        // Открыть файл во внешней программе (с логированием, если не открылось).
        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(path); }
            catch (Exception ex) { AppendLog("Не открылось (файл сохранён): " + path + " - " + ex.Message); }
        }

        // Диалог по завершении предпроверки: что открыть - HTML / Excel / оба / папку.
        private void OfferOpenReport(string html, string xls)
        {
            using (var f = new Form
            {
                Text = "Отчёт предпроверки готов", FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(372, 116)
            })
            {
                try { f.Icon = this.Icon; } catch { }
                f.Controls.Add(new Label { Text = "Что открыть?", Left = 12, Top = 14, Width = 348 });
                var bHtml = new ModernButton { Text = "HTML", Left = 12, Top = 42, Width = 84, Height = 30, Enabled = html != null };
                var bXls = new ModernButton { Text = "Excel", Left = 102, Top = 42, Width = 84, Height = 30, Enabled = xls != null };
                var bBoth = new ModernButton { Text = "Оба", Left = 192, Top = 42, Width = 76, Height = 30, Enabled = html != null && xls != null };
                var bDir = new ModernButton { Text = "Папка", Left = 274, Top = 42, Width = 86, Height = 30 };
                var bCancel = new ModernButton { Text = "Закрыть", Left = 274, Top = 78, Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
                bHtml.Click += (s, e) => { OpenPath(html); f.Close(); };
                bXls.Click += (s, e) => { OpenPath(xls); f.Close(); };
                bBoth.Click += (s, e) => { OpenPath(html); OpenPath(xls); f.Close(); };
                bDir.Click += (s, e) => { OpenReportsFolder(); f.Close(); };
                f.Controls.AddRange(new Control[] { bHtml, bXls, bBoth, bDir, bCancel });
                Theme.Dialog(f);
                f.CancelButton = bCancel;
                f.ShowDialog(this);
            }
        }

        // Открыть папку с отчётами: последний отчёт, если был, иначе общий каталог логов.
        private void OpenReportsFolder()
        {
            string dir = (!string.IsNullOrEmpty(_lastReportDir) && Directory.Exists(_lastReportDir)) ? _lastReportDir : Store.LogsDir;
            try { Directory.CreateDirectory(dir); Process.Start(dir); }
            catch (Exception ex) { AppDialog.Error(this, "Не удалось открыть папку", ex.Message); }
        }

        private void UpdatePreviewRow(HostPreview hp)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(hp.Host ?? "", out row)) return;
            if (!string.IsNullOrEmpty(hp.OsInfo)) row.Cells[Col.Os].Value = hp.OsInfo;
            if (!string.IsNullOrEmpty(hp.Error))
            {
                row.Cells[Col.St].Value = "ошибка"; row.Cells[Col.Note].Value = hp.Error;
                row.DefaultCellStyle.BackColor = Theme.IsDark ? Color.FromArgb(72, 38, 45) : Color.FromArgb(253, 232, 234);
            }
            else
            {
                row.Cells[Col.St].Value = hp.Total + " в транз.";
                row.Cells[Col.Upd].Value = "adv " + hp.Sec + " / завис " + hp.Dep;
                row.Cells[Col.Note].Value = hp.Total > 0 ? ("исключено маской: " + hp.Excluded) : "обновлять нечего (исключено: " + hp.Excluded + ")";
                // зелёный - есть что ставить; голубой - проверено, апдейтов нет (не путать с "не отработало")
                row.DefaultCellStyle.BackColor = hp.Total > 0
                    ? (Theme.IsDark ? Color.FromArgb(28, 66, 49) : Color.FromArgb(229, 247, 237))
                    : Theme.AccentTint;
            }
        }

        private static bool Important(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string tr = line.Trim();
            // сплошные разделители yum (=====, -----) без текста - не показываем
            if (tr.Length >= 8 && (tr.Trim('=').Length == 0 || tr.Trim('-').Length == 0)) return false;
            if (line.StartsWith("===") || line.StartsWith("-----")) return true;
            string[] keys = { "RESULT:", "REBOOT_REQUIRED:", "PRESTOP_RESULT:", "RUNNING_KERNEL:", "EXPECTED_KERNEL:", "VULN|", "VULN_SUMMARY|", "TRIVY_LOG|", "TRIVY_ERR|",
                "Подобрана", "кеш", "Ошибка", "ОШИБКА", "ИСКЛЮЧЕНИЕ", "ВНИМАНИЕ", "Останавли", "reboot", "Reboot", "вернул", "down",
                "Отсутствует", "не подошла", "нет связи", "агрузк", "is-system", "готовности" };
            foreach (var k in keys) if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void UpdateRow(HostResult r, bool starting)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(r.Host ?? "", out row)) return;
            row.Cells[Col.St].Value = starting ? "идёт..." : StatusText(r.Status);
            if (!string.IsNullOrEmpty(r.OsInfo)) row.Cells[Col.Os].Value = r.OsInfo;
            if (!starting)
            {
                row.Cells[Col.Upd].Value = r.UpdateResult;
                row.Cells[Col.Reb].Value = r.RebootAction;
                row.Cells[Col.Pre].Value = r.PreStop;
                row.Cells[Col.Post].Value = r.PostCheck;
                row.Cells[Col.Ker].Value = r.RunningKernel;
                row.Cells[Col.Note].Value = r.Note;
                row.Tag = r.LogFile;
                Color bg = r.Status == HostStatus.Ok ? (Theme.IsDark ? Color.FromArgb(28, 66, 49) : Color.FromArgb(229, 247, 237))
                        : r.Status == HostStatus.Warn ? (Theme.IsDark ? Color.FromArgb(70, 56, 29) : Color.FromArgb(255, 247, 220))
                        : (Theme.IsDark ? Color.FromArgb(72, 38, 45) : Color.FromArgb(253, 232, 234));
                row.DefaultCellStyle.BackColor = bg;
            }
            else row.DefaultCellStyle.BackColor = Theme.AccentTint;
        }

        private void SetRowPhase(string host, string phase)
        {
            DataGridViewRow row;
            if (!_rowByHost.TryGetValue(host ?? "", out row)) return;
            string txt; Color bg;
            switch (phase)
            {
                case "update": txt = "обновление..."; bg = Theme.AccentTint; break;
                case "preview": txt = "предпроверка..."; bg = Theme.AccentTint; break;
                case "prestop": txt = "стоп служб..."; bg = Theme.IsDark ? Color.FromArgb(52, 42, 78) : Color.FromArgb(239, 234, 255); break;
                case "reboot": txt = "перезагрузка..."; bg = Theme.IsDark ? Color.FromArgb(74, 55, 27) : Color.FromArgb(255, 238, 202); break;
                case "postcheck": txt = "проверка..."; bg = Theme.IsDark ? Color.FromArgb(28, 61, 75) : Color.FromArgb(226, 245, 252); break;
                case "scan": txt = "сканирование..."; bg = Theme.AccentTint; break;
                case "repo": txt = "reposync..."; bg = Theme.IsDark ? Color.FromArgb(27, 65, 57) : Color.FromArgb(225, 247, 241); break;
                default: txt = "идёт..."; bg = Theme.AccentTint; break;
            }
            row.Cells[Col.St].Value = txt;
            row.DefaultCellStyle.BackColor = bg;
        }

        // Экранирование поля CSV: переводы строк убираем, при наличии ; " - оборачиваем в кавычки с удвоением.
        private static string Csv(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            if (s.IndexOf('"') >= 0 || s.IndexOf(';') >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string StatusText(HostStatus s)
        {
            switch (s) { case HostStatus.Ok: return "OK"; case HostStatus.Warn: return "WARN"; case HostStatus.Fail: return "FAIL"; default: return "-"; }
        }

        private void WriteSummaryFile(string dir, List<HostResult> res)
        {
            if (res == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Система;Узел;Host;Статус;Обновление;Reboot;Prestop;Postcheck;Ядро;Примечание");
                foreach (var r in res)
                    sb.AppendLine(string.Join(";", new[] { Csv(r.System), Csv(r.Name), Csv(r.Host), Csv(StatusText(r.Status)),
                        Csv(r.UpdateResult), Csv(r.RebootAction), Csv(r.PreStop), Csv(r.PostCheck), Csv(r.RunningKernel), Csv(r.Note) }));
                File.WriteAllText(Path.Combine(dir, "summary.csv"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // Соседний код отчётов предпроверки (PreviewReport.Build/BuildXlsx) логирует свои ошибки
                // через AppendLog - раньше summary.csv тут был единственным исключением, проглатывающим
                // ошибку молча. Вызывается изнутри Ui(), так что AppendLog здесь безопасен (UI-поток).
                AppendLog("Не удалось записать summary.csv: " + ex.Message);
            }
        }

        // ---------- лог по узлам ----------
        private void BufferLog(string host, string line) { BufferLog(host, line, false); }

        // progress=true - строка прогресса reposync: заменяет предыдущую такую же строку, а не удлиняет лог.
        private void BufferLog(string host, string line, bool progress)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            bool replaced;
            lock (_logLock)
            {
                StringBuilder sb;
                if (!_hostLogs.TryGetValue(host, out sb)) { sb = new StringBuilder(); _hostLogs[host] = sb; }
                replaced = progress && _lastLineProgress;
                if (replaced) TrimLastLine(sb);   // затираем прошлую прогресс-строку
                sb.Append(stamped).Append("\r\n");
                if (sb.Length > 1500000) sb.Remove(0, 700000);   // не растём бесконечно на долгом reposync
                _lastLineProgress = progress;   // под тем же локом, что и решение replaced
            }
            bool shown = (_selectedHost == null) || (_selectedHost == host);
            if (!shown) return;
            string screenLine = (_selectedHost == null) ? HostLabel(host) + "  " + stamped : stamped;
            // replaced бывает только для reposync-прогресса (один хост, без чередования) - безопасно править последнюю строку
            if (replaced) Ui(() => ReplaceLastLogLine(screenLine));
            else Ui(() => AppendLog(screenLine));
        }

        // Удалить из буфера последнюю строку (вместе с завершающим \r\n).
        private static void TrimLastLine(StringBuilder sb)
        {
            int len = sb.Length;
            if (len >= 2 && sb[len - 1] == '\n' && sb[len - 2] == '\r') len -= 2;   // отбросить финальный \r\n
            int nl = -1;
            for (int i = len - 1; i >= 0; i--) if (sb[i] == '\n') { nl = i; break; }
            sb.Length = nl + 1;   // оставить всё до предыдущей строки включительно (или 0)
        }

        // Заменить последнюю строку в _log на новую (для прогресс-строки на месте, тем же форматом что и append).
        private void ReplaceLastLogLine(string line)
        {
            string t = _log.Text;
            int end = t.Length;
            if (end >= 2 && t[end - 1] == '\n') end -= 2;   // отбросить финальный \r\n
            int nl = end > 0 ? t.LastIndexOf('\n', end - 1) : -1;
            string keep = nl >= 0 ? t.Substring(0, nl + 1) : "";
            _log.Text = keep + line + "\r\n";
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }

        // Счётчик пакетов reposync в статус-строке (закачано/всего).
        private void SetRepoCount(string host, int done, int total)
        {
            DataGridViewRow row;
            if (_rowByHost.TryGetValue(host ?? "", out row))
                row.Cells[Col.Upd].Value = "пакеты " + done + "/" + total;
            int pct = total > 0 ? (int)(100.0 * done / total) : 0;
            SetStatus("reposync: пакеты " + done + "/" + total + " (" + pct + "%)");
        }
        private void ShowHostLog(string host)
        {
            _selectedHost = host;
            if (_logHint != null) _logHint.Text = "Лог узла: " + HostLabel(host) + "   (кнопка «Все узлы» — общий вид)";
            string text;
            lock (_logLock) { StringBuilder sb; text = _hostLogs.TryGetValue(host, out sb) ? sb.ToString() : ""; }
            _log.Text = text;
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }
        private void ShowAllLogs()
        {
            _selectedHost = null;
            if (_logHint != null) _logHint.Text = "Лог: все узлы. Клик по строке сводки — только её лог.";
            var sb = new StringBuilder();
            lock (_logLock)
                foreach (var kv in _hostLogs)
                { sb.Append("===== ").Append(HostLabel(kv.Key)).Append(" =====\r\n").Append(kv.Value).Append("\r\n"); }
            _log.Text = sb.ToString();
            _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        }

        private string HostLabel(string host)
        {
            foreach (SubSystem system in _cfg.Systems ?? new List<SubSystem>())
                foreach (Node node in system.Nodes ?? new List<Node>())
                    if (string.Equals(node.Host, host, StringComparison.OrdinalIgnoreCase))
                        return NodeLabel(node.Name, node.Host);
            return host ?? "";
        }

        // ---------- утилиты UI ----------
        private void Ui(Action a)
        {
            if (IsDisposed) return;
            // Раньше try/catch ловил исключения только ПОСТАНОВКИ делегата в очередь (BeginInvoke),
            // а не его выполнения - оно происходит асинхронно позже, в цикле сообщений UI-потока.
            // Если внутри a() (например, UpdateRow/UpdatePreviewRow при неожиданном состоянии данных)
            // вылетало исключение, оно уходило как необработанное прямо посреди SSH-операции -
            // потенциальный краш приложения. Оборачиваем сам делегат, не только его постановку.
            Action wrapped = () =>
            {
                try { a(); }
                catch (Exception ex) { try { AppendLog("ОШИБКА ОБНОВЛЕНИЯ UI: " + ex.Message); } catch { } }
            };
            try { if (InvokeRequired) BeginInvoke(wrapped); else wrapped(); }
            catch (ObjectDisposedException) { }      // окно закрыли во время прогона - гасим гонку
            catch (InvalidOperationException) { }
        }
        private void AppendLog(string line)
        {
            if (_log.TextLength > 400000) _log.Text = _log.Text.Substring(_log.TextLength - 200000);
            _log.AppendText(line + "\r\n");
        }
        private void SetStatus(string s)
        {
            _status.SetStatus(s ?? "", ClassifyStatus(s));
            if (_tips != null) _tips.SetToolTip(_status, s ?? "");
            string value = (s ?? "").ToLowerInvariant();
            if (!_running && (value.Contains("сохран") || value.Contains("обновлен") || value.Contains("очищен") ||
                value.Contains("импорт выполнен") || value.Contains("экспортирован") || value.Contains("добавлено")))
                ModernToast.Show(this, s, ToastKind.Success);
        }

        // Определяем цвет статус-чипа по тексту сообщения (сами сообщения не переписываем - их формируют
        // десятки мест в коде). Не находит категорию - остаётся нейтральным (Idle).
        private static StatusChip.Kind ClassifyStatus(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "Готово") return StatusChip.Kind.Idle;
            // Все статусы, которые StartOperation ставит в начале операции, заканчиваются на "..." -
            // проверяем это первым, иначе часть из них (например "Обновление репозитория на host...")
            // по ключевым словам ниже ошибочно попадала бы в Good ("обновлен...") ещё ДО завершения операции.
            if (s.EndsWith("...", StringComparison.Ordinal)
                || s.IndexOf("идёт", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Выполня", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Останавл", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("reposync:", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Busy;
            int fail = CountAfter(s, "FAIL: ");
            if (fail > 0) return StatusChip.Kind.Bad;
            // ": FAIL"/": WARN"/": OK" - формат "Репозиторий: FAIL | ..." (StatusText отдаёт голое
            // "FAIL"/"WARN"/"OK" без счётчика, так что CountAfter с маркером "FAIL: " его не ловит).
            if (s.IndexOf("ошибка", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("не удалось", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf(": FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Bad;
            int warn = CountAfter(s, "WARN: ");
            if (warn > 0 || s.IndexOf(": WARN", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Warn;
            if (fail == 0 && warn == 0
                || s.IndexOf(": OK", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("готов", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("выполнен", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("сохран", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("обновлен", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Экспортировано", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Добавлено", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusChip.Kind.Good;
            return StatusChip.Kind.Idle;
        }

        // Число сразу после маркера ("FAIL: 3" -> 3); маркера нет или после него не число -> -1 (не найдено).
        private static int CountAfter(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += marker.Length;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            int val;
            return (j > i && int.TryParse(s.Substring(i, j - i), out val)) ? val : -1;
        }
        private void SetRunningUi(bool running)
        {
            _btnRun.Enabled = !running; _btnStop.Enabled = running;
            _btnPreview.Enabled = !running;
            _profile.Enabled = !running; _noReboot.Enabled = !running;
            if (_pkgBox != null) _pkgBox.Enabled = !running;
            // на время прогона блокируем правку конфига и дерева
            if (_leftPanel != null) _leftPanel.Enabled = !running;
            _configurationControls.RemoveAll(control => control == null || control.IsDisposed);
            foreach (Control control in _configurationControls)
                if (control != null && !control.IsDisposed) control.Enabled = !running;
            // Исключения фиксируются в RunOptions при старте, поэтому во время операции их не меняем.
            if (_excluded != null) _excluded.Enabled = !running;
        }

        // Компоненты без визуального родителя (ToolTip не кладётся в Controls, CancellationTokenSource
        // не привязан к WinForms Component-дереву вовсе) не освобождаются автоматически при закрытии
        // формы - раньше здесь не было override Dispose, и оба этих объекта просто утекали.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tips != null) _tips.Dispose();
                if (_nodeActionsMenu != null) _nodeActionsMenu.Dispose();
                if (_cts != null) _cts.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                if (!_closeAfterOperation && !AppDialog.Confirm(this, "Завершение работы", "Сейчас выполняется операция. Остановить её и закрыть приложение?", "Остановить и выйти"))
                { e.Cancel = true; return; }
                _closeAfterOperation = true;
                if (_cts != null) _cts.Cancel();
                SetStatus("Останавливаю перед выходом...");
                e.Cancel = true;
                return;
            }
            try { Store.SaveConfig(_cfg); } catch { }
            base.OnFormClosing(e);
        }
    }
}
