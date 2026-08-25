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
        private ModernSearchBox _treeSearch;
        private ComboBox _profile;
        private CheckBox _noReboot;
        private ToolTip _tips;
        private ContextMenuStrip _nodeActionsMenu;
        private Label _pkgLabel;
        private ModernTextBox _pkgBox;
        private Button _btnRun, _btnStop, _btnPreview, _btnVulnerabilityScan;
        private Button _btnEditSelection, _btnSystemServices, _btnToggleAll;
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
        private ModernSearchBox _summarySearch;

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
            EnableModernWindowChrome();
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
                string seed = Profiles.TryRead("seed_config.json");
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
            // Без внешней карточки: нативная фактическая высота ComboBox зависит от DPI и темы,
            // а прежний контейнер высотой 30px обрезал его нижнюю границу.
            var profileBox = new Panel { Left = 12, Top = 28, Width = 360, Height = 32, BackColor = Theme.Surface };
            top.Controls.Add(profileBox);
            _profile = new ModernComboBox { Dock = DockStyle.Fill };
            Theme.Combo(_profile);
            foreach (OperationScenario scenario in OperationScenario.All) _profile.Items.Add(scenario);
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
            _pkgLabel = new Label { Left = 12, Top = 66, Width = 170, Height = 20, Text = "Пакеты:", Visible = false, ForeColor = Theme.Muted, Font = Theme.UiFontSmall, TextAlign = ContentAlignment.MiddleLeft };
            top.Controls.Add(_pkgLabel);
            _pkgBox = new ModernTextBox { Left = 188, Top = 61, Width = 400, Height = 28, Visible = false, Font = Theme.Mono, Placeholder = "Имя пакета или пакет-версия" };
            top.Controls.Add(_pkgBox);
            _btnPreview = new ModernButton { Width = 146, Height = 32, Text = "Проверить изменения", IconName = "search" };
            _btnPreview.Click += (s, e) => PreviewChecked();
            _tips.SetToolTip(_btnPreview, "Без установки показывает реальную транзакцию DNF: пакеты, зависимости и исключения");
            Theme.Secondary(_btnPreview);
            top.Controls.Add(_btnPreview);
            _btnRun = new ModernButton { Width = 174, Height = 32, Text = "Запустить отмеченные", IconName = "play" };
            _btnRun.Click += (s, e) => RunChecked();
            _tips.SetToolTip(_btnRun, "Выполнить выбранный сценарий на отмеченных серверах");
            Theme.Primary(_btnRun);
            top.Controls.Add(_btnRun);
            _btnStop = new ModernButton { Width = 94, Height = 32, Text = "Остановить", Enabled = false, IconName = "stop" };
            _btnStop.Click += (s, e) => { if (_cts != null) _cts.Cancel(); SetStatus("Останавливаю..."); };
            _tips.SetToolTip(_btnStop, "Отменить новые шаги операции и дождаться завершения уже выполняющихся команд");
            Theme.Danger_(_btnStop);
            top.Controls.Add(_btnStop);
            _status = new StatusChip { Width = 236, Height = 28 };
            _status.SetStatus("Готово", StatusChip.Kind.Idle);
            top.Controls.Add(_status);
            _excluded = new Label { Left = 190, Top = 65, Width = 900, Height = 18, ForeColor = Theme.Danger, Cursor = Cursors.Hand, Text = "", AutoEllipsis = true };
            _excluded.Click += (s, e) => EditExclusions();
            top.Controls.Add(_excluded);
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
            var treeHeader = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.SidebarBg };
            var treeActions = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.SidebarBg };
            var treeTitle = Theme.SectionLabel("Серверы");
            treeTitle.Left = 2; treeTitle.Top = 5; treeTitle.Width = 150; treeTitle.Height = 20;
            _btnToggleAll = Theme.ToolbarButton("Отметить все", 104);
            _btnToggleAll.Dock = DockStyle.Right;
            _btnToggleAll.Click += (s, e) =>
            {
                int total = 0, selected = 0;
                foreach (TreeNode system in _tree.Nodes)
                    foreach (TreeNode node in system.Nodes) { total++; if (node.Checked) selected++; }
                CheckAll(total == 0 || selected < total);
            };
            _tips.SetToolTip(_btnToggleAll, "Отметить все серверы");
            treeActions.Controls.Add(treeTitle);
            treeActions.Controls.Add(_btnToggleAll);
            _treeSearch = new ModernSearchBox { Left = 0, Top = 36, Width = Math.Max(1, treeHeader.ClientSize.Width),
                Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Placeholder = "Поиск серверов…" };
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
            var addSystem = AddCompactBtn(leftButtons, "Группа", 92, () => AddSystem()); ((ModernButton)addSystem).IconName = "add";
            var addNode = AddCompactBtn(leftButtons, "Узел", 78, () => AddNode()); ((ModernButton)addNode).IconName = "add";
            _nodeActionsMenu = new ContextMenuStrip();
            Theme.ContextMenu(_nodeActionsMenu);
            _nodeActionsMenu.Items.Add("Массовый ввод узлов", null, (s, e) => BulkNodes());
            _nodeActionsMenu.Items.Add(new ToolStripSeparator());
            _nodeActionsMenu.Items.Add("Изменить", null, (s, e) => EditSelected());
            _nodeActionsMenu.Items.Add("Службы перед перезагрузкой", null, (s, e) => EditServices());
            _nodeActionsMenu.Items.Add(new ToolStripSeparator());
            _nodeActionsMenu.Items.Add("Удалить", null, (s, e) => DeleteSelected());
            Button more = null;
            more = AddCompactBtn(leftButtons, "Ещё", 72, delegate { _nodeActionsMenu.Show(more, new Point(0, more.Height)); });
            ((ModernButton)more).IconName = "more";
            more.AccessibleName = "Ещё действия";
            _tips.SetToolTip(more, "Ещё действия с серверами");
            var treeHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            _treeEmpty = new Label { Dock = DockStyle.Fill, Text = "Серверов пока нет\r\n\r\nДобавьте группу серверов, затем первый сервер", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Surface, Font = Theme.UiFontBodyLarge, Visible = false };
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
            _summarySearch = new ModernSearchBox { Dock = DockStyle.Right, Width = 226, Height = 26, Placeholder = "Поиск по результатам…", Margin = new Padding(0, 0, 8, 0) };
            _summarySearch.TextChanged += delegate { FilterSummaryRows(); };
            _tips.SetToolTip(_summarySearch, "Поиск по всем колонкам результатов · Ctrl+F · Esc для очистки");
            gridHeader.Controls.Add(_summarySearch); _summarySearch.BringToFront();
            AddCol(Col.System, "Система", 128); AddCol(Col.Name, "Узел", 150); AddCol(Col.Host, "IP / имя", 112);
            AddCol(Col.St, "Статус", 92); AddCol(Col.Upd, "Результат", 110); AddCol(Col.Reb, "Перезагрузка", 104);
            AddCol(Col.Pre, "До обновления", 108); AddCol(Col.Post, "После обновления", 118); AddCol(Col.Ker, "Ядро", 150);
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
            var btnAllLogs = new ModernButton { Text = "Общий журнал", Left = 0, Top = 3, Width = 112, Height = 28 };
            Theme.Secondary(btnAllLogs);
            btnAllLogs.Click += (s, e) => ShowAllLogs();
            var btnReports = new ModernButton { Text = "Открыть отчёты", Left = 118, Top = 3, Width = 126, Height = 28 };
            Theme.Secondary(btnReports);
            btnReports.Click += (s, e) => OpenReportsFolder();
            _logHint = new Label { Left = 254, Top = 9, Width = 520, Text = "Выберите строку результата, чтобы видеть журнал только этого сервера.", ForeColor = Theme.Muted };
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
                    if (si != null)
                    using (var loadedIcon = new System.Drawing.Icon(si))
                        Icon = (System.Drawing.Icon)loadedIcon.Clone();
            }
            catch { }

            UpdateModeUi();   // начальная видимость поля пакетов / строки исключений
        }

        // Режим "пакеты" (Установить/Обновить пакеты) выбран в списке профиля.
        private OperationScenario SelectedScenario { get { return _profile.SelectedItem as OperationScenario ?? OperationScenario.All[0]; } }
        private bool IsPkgMode() { return SelectedScenario.IsPackageOperation; }
        private string PkgAction()
        {
            return SelectedScenario.PackageAction;
        }
        // человекочитаемое имя действия для заголовков/подтверждений
        private static string ActionRu(string action)
        {
            return OperationDomain.ActionTitle(action);
        }

        private void UpdateModeUi()
        {
            bool pkg = IsPkgMode();
            if (_pkgLabel != null)
            {
                _pkgLabel.Visible = pkg;
                // для просмотра блокировок поле пакетов - необязательный фильтр (пусто = все)
                _pkgLabel.Text = SelectedScenario.PackageFilterOptional ? "Фильтр (необязательно):" : "Пакеты:";
            }
            if (_pkgBox != null) _pkgBox.Visible = pkg;
            if (_noReboot != null) _noReboot.Visible = !pkg;   // для пакетов reboot не делаем
            if (_excluded != null) _excluded.Visible = !pkg;   // исключения к явной установке не относятся
        }

        // Список пакетов из поля (через пробел/строки), пусто -> null.
        private string PkgListFromBox()
        {
            return OperationDomain.NormalizePackageList(_pkgBox.Text);
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
            // Панель ФСТЭК создаётся позже, когда собирается application shell.
            if (_fstecProgress != null && _fstecProgressLabel != null)
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
            if (!HasUsableCredentials()) { AppDialog.Info(this, "Нет доступных учётных записей", "Добавьте или повторно введите учётную запись в разделе «Доступ и SSH»."); return; }
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


    }
}
