using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    public partial class MainForm
    {
        private Label _serverDetailTitle, _serverDetailBody;
        private Label _shellStatus, _fstecDbState;

        private void BuildApplicationShell(Panel operationBar, Panel serverTree, SplitContainer operationContent)
        {
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 31, BackColor = Theme.Surface };
            Theme.EdgeLine(footer, DockStyle.Top);
            var readyDot = new Panel { Left = 14, Top = 12, Width = 7, Height = 7, BackColor = Theme.Good };
            _shellStatus = new Label { Left = 29, Top = 7, Width = 560, Height = 18, Text = "Готово", ForeColor = Theme.Muted };
            var version = new Label { Dock = DockStyle.Right, Width = 230, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 10, 0), Text = "RED OS Package Updater " + AppUpdater.CurrentVersion, ForeColor = Theme.Muted };
            footer.Controls.Add(readyDot); footer.Controls.Add(_shellStatus); footer.Controls.Add(version);

            var navigation = new Panel { Dock = DockStyle.Left, Width = 214, BackColor = Theme.NavigationBg, Padding = new Padding(12, 12, 12, 12) };
            var brand = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.NavigationBg };
            var brandMark = new AppIconView { Left = 4, Top = 4, Width = 36, Height = 36, BackColor = Theme.NavigationBg };
            var brandName = new Label { Left = 50, Top = 4, Width = 138, Height = 23, Text = "RED OS UPDATER", ForeColor = Color.White, Font = Theme.UiFontBrand };
            var brandSub = new Label { Left = 50, Top = 27, Width = 138, Height = 19, Text = "Центр управления", ForeColor = Theme.NavigationText, Font = Theme.UiFontBrandSmall };
            var navCaption = new Label { Left = 4, Top = 54, Width = 184, Height = 18, Text = "РАЗДЕЛЫ", ForeColor = Color.FromArgb(124, 142, 169), Font = Theme.UiFontSmall };
            brand.Controls.Add(brandMark); brand.Controls.Add(brandName); brand.Controls.Add(brandSub); brand.Controls.Add(navCaption);

            var bottomNav = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 92, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.NavigationBg };
            var navFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.NavigationBg, Padding = new Padding(0, 4, 0, 0), AutoScroll = true };
            navigation.Controls.Add(navFlow);
            navigation.Controls.Add(bottomNav);
            navigation.Controls.Add(brand);
            AddNavigationButton(navFlow, "servers", "Серверы", delegate { ShowApplicationPage("servers"); });
            AddNavigationButton(navFlow, "operations", "Операции", delegate { ShowApplicationPage("operations"); });
            AddNavigationButton(navFlow, "fstec", "Уязвимости ФСТЭК", delegate { ShowApplicationPage("fstec"); });
            AddNavigationButton(navFlow, "reports", "Отчёты", delegate { ShowApplicationPage("reports"); });
            AddNavigationButton(navFlow, "access", "Доступ и SSH", delegate { ShowApplicationPage("access"); });
            AddNavigationButton(bottomNav, "settings", "Настройки", delegate { ShowApplicationPage("settings"); });
            AddNavigationButton(bottomNav, "more", "Дополнительно", delegate { ShowShellMenu(_navigationButtons["more"]); });

            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0) };
            _serversPage = BuildServersPage(serverTree);
            _operationsPage = BuildOperationsPage(operationBar, operationContent);
            _fstecPage = BuildFstecPage();
            _reportsPage = BuildReportsPage();
            _accessPage = BuildAccessPage();
            _settingsPage = BuildSettingsPage();
            _pageHost.Controls.Add(_serversPage);
            _pageHost.Controls.Add(_operationsPage);
            _pageHost.Controls.Add(_fstecPage);
            _pageHost.Controls.Add(_reportsPage);
            _pageHost.Controls.Add(_accessPage);
            _pageHost.Controls.Add(_settingsPage);

            Controls.Add(_pageHost);
            Controls.Add(navigation);
            Controls.Add(footer);
            _tree.AfterSelect += delegate { RefreshServerDetails(); };
            ShowApplicationPage("servers");
            RefreshSelectionSummary();
            RefreshServerDetails();
        }

        private Panel BuildServersPage(Panel serverTree)
        {
            var page = NewPage();
            var head = BuildPageHeader("Серверы", "Инфраструктура, состояние и выбор целей", "Добавить узел", delegate { AddNode(); }, true);
            page.Controls.Add(head);

            var body = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 320, FixedPanel = FixedPanel.Panel1, SplitterWidth = 8, BackColor = Theme.Bg };
            body.Panel1.Padding = new Padding(18, 16, 4, 18);
            body.Panel2.Padding = new Padding(4, 16, 18, 18);
            serverTree.Dock = DockStyle.Fill;
            body.Panel1.Controls.Add(serverTree);
            _workspaceSplit = body;
            body.Resize += delegate { LayoutServerWorkspace(body); };

            var inventory = new ModernCard { Dock = DockStyle.Top, Height = 292, BackColor = Theme.Surface, Padding = new Padding(22) };
            Theme.Box(inventory);
            var detailCaption = Theme.SectionLabel("Выбранный объект"); detailCaption.Dock = DockStyle.Top; detailCaption.Height = 24;
            _serverDetailTitle = new Label { Dock = DockStyle.Top, Height = 40, Font = Theme.UiFontPageTitle, ForeColor = Theme.Text };
            _serverDetailBody = new Label { Dock = DockStyle.Top, Height = 142, ForeColor = Theme.Text, Padding = new Padding(0, 8, 0, 0), Font = Theme.UiFontBodyLarge };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            _btnEditSelection = AddCompactBtn(actions, "Изменить", 90, delegate { EditSelected(); });
            _btnSystemServices = AddCompactBtn(actions, "Сервисы системы", 126, delegate { EditServices(); });
            var newOperation = AddCompactBtn(actions, "Новая операция", 120, delegate { ShowApplicationPage("operations"); }); Theme.Primary(newOperation);
            inventory.Controls.Add(actions); inventory.Controls.Add(_serverDetailBody); inventory.Controls.Add(_serverDetailTitle); inventory.Controls.Add(detailCaption);
            var guidance = new ModernCard { Dock = DockStyle.Top, Height = 108, BackColor = Theme.AccentTint, Padding = new Padding(18, 15, 18, 12) };
            var guidanceTitle = new Label { Dock = DockStyle.Top, Height = 24, Text = "Быстрый старт", Font = Theme.UiFontBold, ForeColor = Theme.AccentDown };
            var guidanceText = new Label { Dock = DockStyle.Fill, Text = "1. Отметьте нужные серверы слева\r\n2. Перейдите в «Операции» или «Уязвимости ФСТЭК»\r\n3. Выполните предпроверку перед изменениями", ForeColor = Theme.Text };
            guidance.Controls.Add(guidanceText); guidance.Controls.Add(guidanceTitle);
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            right.Controls.Add(guidance); right.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg }); right.Controls.Add(inventory);
            body.Panel2.Controls.Add(right);
            page.Controls.Add(body); body.BringToFront();
            return page;
        }

        private Panel BuildOperationsPage(Panel operationBar, SplitContainer operationContent)
        {
            var page = NewPage();
            var head = BuildPageHeader("Операции", "Обновление пакетов, ядра и управление версиями", "Изменить выбор", delegate { ShowApplicationPage("servers"); });
            _selectionLabel = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), ForeColor = Theme.Muted, BackColor = Theme.Surface };
            head.Controls.Add(_selectionLabel);
            head.Height = 72;
            operationBar.Dock = DockStyle.Top;
            operationContent.Dock = DockStyle.Fill;
            page.Controls.Add(operationContent);
            page.Controls.Add(operationBar);
            page.Controls.Add(head);
            return page;
        }

        private Panel BuildFstecPage()
        {
            var page = NewPage();
            var head = BuildPageHeader("Уязвимости ФСТЭК", "Бюллетени безопасности RED OS с локальным сопоставлением CVE и БДУ", "Обновить базу", delegate { UpdateVulnerabilityDb(); }, true);
            page.Controls.Add(head);
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 14), BackColor = Theme.Bg, AutoScroll = true };
            var db = new ModernCard { Dock = DockStyle.Top, Height = 132, BackColor = Theme.Surface, Padding = new Padding(14) }; Theme.Box(db);
            var dbTitle = new Label { Dock = DockStyle.Top, Height = 24, Text = "Локальная база уязвимостей", Font = Theme.UiFontBold };
            _fstecDbState = new Label { Dock = DockStyle.Top, Height = 25, Text = VulnerabilityDb.StatusText(), ForeColor = FstecLinuxCatalog.Exists ? Theme.Good : Theme.Warn };
            var dbHint = new Label { Dock = DockStyle.Top, Height = 34, Text = "Узлы читают только локальные метаданные DNF. CVE связываются с каталогом БДУ на управляющем компьютере; интернет на серверах не требуется.", ForeColor = Theme.Muted };
            _fstecProgressLabel = new Label { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Visible = false };
            _fstecProgress = new ModernProgressBar { Dock = DockStyle.Bottom, Height = 8, Minimum = 0, Maximum = 100, Visible = false };
            db.Controls.Add(_fstecProgress); db.Controls.Add(_fstecProgressLabel); db.Controls.Add(dbHint); db.Controls.Add(_fstecDbState); db.Controls.Add(dbTitle);

            var launch = new ModernCard { Dock = DockStyle.Top, Height = 112, BackColor = Theme.Surface, Padding = new Padding(14) }; Theme.Box(launch);
            var launchTitle = new Label { Dock = DockStyle.Top, Height = 26, Text = "Новая проверка", Font = Theme.UiFontBold };
            var launchHint = new Label { Dock = DockStyle.Top, Height = 27, Text = "Будут проверены выбранные на странице «Серверы» узлы.", ForeColor = Theme.Muted };
            var launchButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _btnVulnerabilityScan = LockConfiguration(AddCompactBtn(launchButtons, "Проверить выбранные", 180, delegate { RunVulnerabilityScan(); ShowApplicationPage("operations"); })); Theme.Primary(_btnVulnerabilityScan);
            LockConfiguration(AddCompactBtn(launchButtons, "Импортировать базу", 142, delegate { ImportVulnerabilityDb(); }));
            launch.Controls.Add(launchButtons); launch.Controls.Add(launchHint); launch.Controls.Add(launchTitle);
            var method = new TableLayoutPanel { Dock = DockStyle.Top, Height = 138, ColumnCount = 3, Padding = new Padding(0, 10, 0, 0), BackColor = Theme.Bg };
            method.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); method.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); method.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            method.Controls.Add(InfoCard("1 · RED OS", "DNF сообщает доступные бюллетени безопасности и исправленные версии", "ТОЛЬКО ЧТЕНИЕ"), 0, 0);
            method.Controls.Add(InfoCard("2 · БДУ ФСТЭК", "CVE сопоставляются с локальным официальным каталогом и применимостью", "АВТОНОМНО"), 1, 0);
            method.Controls.Add(InfoCard("3 · Отчёт", "Записи группируются по пакету и единому действию обновления", "HTML · CSV"), 2, 0);
            content.Controls.Add(method); content.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg });
            content.Controls.Add(launch); content.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg }); content.Controls.Add(db);
            page.Controls.Add(content); content.BringToFront();
            return page;
        }

        private Panel BuildReportsPage()
        {
            var page = NewPage();
            var head = BuildPageHeader("Отчёты", "История операций и проверок уязвимостей", "Открыть папку", delegate { OpenReportsFolder(); });
            page.Controls.Add(head);
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 14), BackColor = Theme.Bg, AutoScroll = true };
            var info = new ModernCard { Dock = DockStyle.Top, Height = 86, BackColor = Theme.Surface, Padding = new Padding(18) }; Theme.Box(info);
            var title = new Label { Dock = DockStyle.Top, Height = 26, Text = "Все результаты собраны в одном месте", Font = Theme.UiFontHeadingLarge };
            var path = new Label { Dock = DockStyle.Fill, Text = "Каталог хранения: " + Store.LogsDir, ForeColor = Theme.Muted };
            info.Controls.Add(path); info.Controls.Add(title);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(0, 14, 0, 0), BackColor = Theme.Bg };
            AddCompactBtn(actions, "Последний отчёт", 124, delegate { OpenReportsFolder(); });
            AddCompactBtn(actions, "Логи операций", 118, delegate { OpenPath(Store.LogsDir); });
            var kinds = new TableLayoutPanel { Dock = DockStyle.Top, Height = 138, ColumnCount = 3, Padding = new Padding(0, 12, 0, 0), BackColor = Theme.Bg };
            kinds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); kinds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F)); kinds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            kinds.Controls.Add(InfoCard("Предпроверки", "Состав транзакции до внесения изменений", "CSV"), 0, 0);
            kinds.Controls.Add(InfoCard("Операции", "Сводка по узлам и подробные журналы", "LOG"), 1, 0);
            kinds.Controls.Add(InfoCard("Уязвимости", "Подтверждённые БДУ и полный набор кандидатов", "HTML · CSV"), 2, 0);
            content.Controls.Add(BuildRecentReportsCard()); content.Controls.Add(kinds); content.Controls.Add(actions); content.Controls.Add(info);
            page.Controls.Add(content); content.BringToFront();
            return page;
        }

        private Panel BuildAccessPage()
        {
            var page = NewPage();
            var head = BuildPageHeader("Доступ и SSH", "Учётные записи, кэш подключений и доверенные ключи", "Добавить учётку", delegate { EditCredentials(); RefreshAccessPage(); }, true);
            page.Controls.Add(head);
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 14), BackColor = Theme.Bg };
            var accounts = new ModernCard { Name = "accounts", Dock = DockStyle.Top, Height = 178, BackColor = Theme.Surface, Padding = new Padding(14) }; Theme.Box(accounts);
            content.Controls.Add(accounts);
            var ssh = new ModernCard { Dock = DockStyle.Top, Height = 118, BackColor = Theme.Surface, Padding = new Padding(14) }; Theme.Box(ssh);
            var sshTitle = new Label { Dock = DockStyle.Top, Height = 25, Text = "SSH-ключи серверов", Font = Theme.UiFontBold };
            var sshHint = new Label { Dock = DockStyle.Top, Height = 32, Text = "Неизвестные ключи подтверждаются оператором. Для массовой операции подтверждение может действовать на весь текущий запуск.", ForeColor = Theme.Muted };
            var sshActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            LockConfiguration(AddCompactBtn(sshActions, "Управление ключами", 146, delegate { ManageHostKeys(); }));
            LockConfiguration(AddCompactBtn(sshActions, "Очистить кэш учёток", 154, delegate { _cache.Clear(); Store.SaveCache(_cache); SetStatus("Кэш учёток очищен"); }));
            ssh.Controls.Add(sshActions); ssh.Controls.Add(sshHint); ssh.Controls.Add(sshTitle);
            content.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg }); content.Controls.Add(ssh); ssh.BringToFront();
            page.Controls.Add(content); content.BringToFront();
            RefreshAccessPanel(accounts);
            return page;
        }

        private Panel BuildSettingsPage()
        {
            var page = NewPage();
            var head = BuildPageHeader("Настройки", "Параметры выполнения, хранения и обновления программы", "Изменить", delegate { EditSettings(); RefreshSettingsPage(); }, true);
            page.Controls.Add(head);
            var content = new Panel { Name = "settingsContent", Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 14), BackColor = Theme.Bg, AutoScroll = true };
            page.Controls.Add(content); content.BringToFront();
            RefreshSettingsPanel(content);
            return page;
        }

        private void RefreshAccessPage()
        {
            if (_accessPage == null) return;
            var accounts = FindControl(_accessPage, "accounts") as Panel;
            if (accounts != null) RefreshAccessPanel(accounts);
        }

        private void RefreshAccessPanel(Panel accounts)
        {
            DisposeChildren(accounts);
            var title = new Label { Dock = DockStyle.Top, Height = 25, Text = "Учётные записи SSH · " + _cfg.Credentials.Count, Font = Theme.UiFontBold };
            var hint = new Label { Dock = DockStyle.Top, Height = 30, Text = "Пароли скрыты. Подходящая учётная запись кэшируется отдельно для каждого узла.", ForeColor = Theme.Muted };
            var rows = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text };
            var lines = new System.Collections.Generic.List<string>();
            foreach (var c in _cfg.Credentials) lines.Add((string.IsNullOrEmpty(c.User) ? "root" : c.User) + "     ••••••••");
            rows.Text = lines.Count == 0 ? "Учётные записи ещё не добавлены." : string.Join(Environment.NewLine, lines.ToArray());
            accounts.Controls.Add(rows); accounts.Controls.Add(hint); accounts.Controls.Add(title);
        }

        private void RefreshSettingsPage()
        {
            if (_settingsPage == null) return;
            var content = FindControl(_settingsPage, "settingsContent") as Panel;
            if (content != null) RefreshSettingsPanel(content);
        }

        private void RefreshSettingsPanel(Panel content)
        {
            DisposeChildren(content);
            var execution = SettingsCard("Выполнение", new string[] {
                "Одновременных узлов: " + _cfg.Settings.MaxParallel,
                "Таймаут SSH: " + _cfg.Settings.ConnectTimeoutSec + " сек.",
                "Таймаут DNF: " + _cfg.Settings.UpdateTimeoutSec + " сек.",
                "Попыток авторизации: " + (_cfg.Settings.MaxAuthAttempts == 0 ? "все" : _cfg.Settings.MaxAuthAttempts.ToString())
            });
            var reboot = SettingsCard("Перезагрузка и сервисы", new string[] {
                "Пауза после перезагрузки: " + _cfg.Settings.InitialRebootDelaySec + " сек.",
                "Ожидание возврата: " + _cfg.Settings.UpTimeoutSec + " сек.",
                "Остановка сервиса: " + _cfg.Settings.StopServiceTimeoutSec + " сек.",
                "Бэкапов на узле: " + _cfg.Settings.BackupKeep
            });
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 182, ColumnCount = 2, BackColor = Theme.Bg, Padding = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            execution.Dock = DockStyle.Fill; execution.Margin = new Padding(0, 0, 6, 0);
            reboot.Dock = DockStyle.Fill; reboot.Margin = new Padding(6, 0, 0, 0);
            grid.Controls.Add(execution, 0, 0); grid.Controls.Add(reboot, 1, 0); content.Controls.Add(grid);
            var update = SettingsCard("Обновление программы", new string[] { "Текущая версия: " + AppUpdater.CurrentVersion, "Источник: GitHub / ozzf1ghter" });
            update.Dock = DockStyle.Top; update.Height = 154; update.Margin = new Padding(0, 12, 0, 0); content.Controls.Add(update); update.BringToFront();
            var updateActions = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Theme.Surface };
            var updateHint = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 8, 0), Text = "Проверка и установка доступны в разделе «О программе»", ForeColor = Theme.Muted, AutoEllipsis = true };
            var theme = new ModernButton { Text = Theme.IsDark ? "Светлая тема" : "Тёмная тема", Dock = DockStyle.Right, Width = 144, Height = 32 };
            Theme.Secondary(theme); theme.Click += delegate { ToggleUiTheme(); };
            LockConfiguration(theme);
            updateActions.Controls.Add(updateHint); updateActions.Controls.Add(theme);
            update.Controls.Add(updateActions); updateActions.BringToFront();
        }

        private Panel SettingsCard(string title, string[] rows)
        {
            var card = new ModernCard { Width = 372, Height = 166, BackColor = Theme.Surface, Padding = new Padding(14) }; Theme.Box(card);
            var heading = new Label { Dock = DockStyle.Top, Height = 26, Text = title, Font = Theme.UiFontBold };
            var body = new Label { Dock = DockStyle.Fill, Text = string.Join(Environment.NewLine + Environment.NewLine, rows), ForeColor = Theme.Text };
            card.Controls.Add(body); card.Controls.Add(heading); return card;
        }

        private Panel InfoCard(string title, string text, string badge)
        {
            var card = new ModernCard { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(16) }; Theme.Box(card);
            var mark = new Label { Dock = DockStyle.Right, Width = 116, Height = 24, Text = badge, TextAlign = ContentAlignment.TopRight, ForeColor = Theme.Accent, Font = Theme.UiFontBold, AutoEllipsis = true };
            var heading = new Label { Dock = DockStyle.Top, Height = 26, Text = title, Font = Theme.UiFontHeading, ForeColor = Theme.Text };
            var body = new Label { Dock = DockStyle.Fill, Text = text, ForeColor = Theme.Muted, Padding = new Padding(0, 8, 6, 0) };
            card.Controls.Add(body); card.Controls.Add(mark); card.Controls.Add(heading); return card;
        }

        private static Control FindControl(Control parent, string name)
        {
            if (parent.Name == name) return parent;
            foreach (Control child in parent.Controls) { var found = FindControl(child, name); if (found != null) return found; }
            return null;
        }

        private static void DisposeChildren(Control parent)
        {
            while (parent.Controls.Count > 0)
            {
                Control child = parent.Controls[0];
                parent.Controls.RemoveAt(0);
                child.Dispose();
            }
        }

        private static Panel NewPage()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
        }

        private Panel BuildPageHeader(string title, string subtitle, string actionText, Action action, bool lockWhileRunning = false)
        {
            var head = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Surface, Padding = new Padding(18, 12, 18, 10) };
            Theme.EdgeLine(head, DockStyle.Bottom);
            var titleLabel = new Label { Left = 18, Top = 12, Width = 500, Height = 28, Text = title, Font = Theme.UiFontPageTitle, ForeColor = Theme.Text };
            var subtitleLabel = new Label { Left = 19, Top = 43, Width = 650, Height = 20, Text = subtitle, ForeColor = Theme.Muted };
            var actionButton = new ModernButton { Dock = DockStyle.Right, Width = 146, Text = actionText, Margin = new Padding(4) };
            Theme.Secondary(actionButton); actionButton.Click += delegate { if (action != null) action(); };
            if (lockWhileRunning) LockConfiguration(actionButton);
            head.Controls.Add(actionButton); head.Controls.Add(subtitleLabel); head.Controls.Add(titleLabel);
            Action layout = delegate
            {
                int available = Math.Max(120, actionButton.Left - 28);
                titleLabel.Width = available;
                subtitleLabel.Width = available;
            };
            head.Resize += delegate { layout(); };
            layout();
            return head;
        }

        private T LockConfiguration<T>(T control) where T : Control
        {
            if (control != null && !_configurationControls.Contains(control)) _configurationControls.Add(control);
            return control;
        }

        private void AddNavigationButton(Control parent, string key, string text, Action action)
        {
            var button = new ModernButton { Width = 188, Height = 42, Text = text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(40, 0, 0, 0), Margin = new Padding(0, 2, 0, 2), BackColor = Theme.NavigationBg, ForeColor = Theme.NavigationText, Cursor = Cursors.Hand, Font = Theme.UiFont, Tag = false, Kind = ModernButtonKind.Navigation, NavigationIcon = key, CornerRadius = 6 };
            button.AccessibleName = text;
            button.AccessibleDescription = "Открыть раздел «" + text + "»";
            button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Theme.NavigationHover;
            button.Click += delegate { if (action != null) action(); };
            if (_tips != null)
            {
                string shortcut = key == "servers" ? "Alt+1" : key == "operations" ? "Alt+2" : key == "fstec" ? "Alt+3" :
                    key == "reports" ? "Alt+4" : key == "access" ? "Alt+5" : key == "settings" ? "Alt+6" : "";
                _tips.SetToolTip(button, text + (shortcut.Length > 0 ? "  ·  " + shortcut : ""));
            }
            _navigationButtons[key] = button; parent.Controls.Add(button);
        }

        private void ShowApplicationPage(string key)
        {
            Panel target = key == "servers" ? _serversPage :
                key == "fstec" ? _fstecPage :
                key == "reports" ? _reportsPage :
                key == "access" ? _accessPage :
                key == "settings" ? _settingsPage : _operationsPage;
            if (target == null) return;
            foreach (Control page in _pageHost.Controls) page.Visible = page == target;
            target.BringToFront();
            foreach (var pair in _navigationButtons)
            {
                bool active = pair.Key == key;
                pair.Value.BackColor = active ? Theme.NavigationActive : Theme.NavigationBg;
                pair.Value.ForeColor = Color.White;
                pair.Value.Tag = active;
                var modern = pair.Value as ModernButton; if (modern != null) modern.NavigationActive = active;
                pair.Value.Invalidate();
            }
            RefreshSelectionSummary();
        }

        private void RefreshSelectionSummary()
        {
            if (_tree == null) return;
            int total = 0, selected = 0;
            foreach (TreeNode system in _tree.Nodes)
                foreach (TreeNode node in system.Nodes) { total++; if (node.Checked) selected++; }
            if (_selectionLabel != null) _selectionLabel.Text = "Выбрано серверов: " + selected + " из " + total;
            if (_shellStatus != null) _shellStatus.Text = _running ? "Выполняется операция" : "Готово · выбрано " + selected + " серверов";
            if (_btnRun != null && !_running) _btnRun.Text = selected > 0 ? "Запустить на " + selected + " узлах" : "Запустить отмеченные";
            if (!_running)
            {
                if (_btnRun != null) _btnRun.Enabled = selected > 0;
                if (_btnPreview != null) _btnPreview.Enabled = selected > 0;
                if (_btnVulnerabilityScan != null) _btnVulnerabilityScan.Enabled = selected > 0;
            }
        }

        private void RefreshServerDetails()
        {
            if (_serverDetailTitle == null) return;
            var selected = _tree.SelectedNode;
            var node = selected == null ? null : selected.Tag as Node;
            var system = selected == null ? null : selected.Tag as SubSystem;
            if (_btnEditSelection != null) _btnEditSelection.Enabled = selected != null && !_running;
            if (_btnSystemServices != null) _btnSystemServices.Enabled = selected != null && !_running;
            if (node != null)
            {
                _serverDetailTitle.Text = string.IsNullOrEmpty(node.Name) ? node.Host : node.Name;
                var parentSystem = selected.Parent == null ? "—" : ((SubSystem)selected.Parent.Tag).Name;
                _serverDetailBody.Text = "Система\r\n" + parentSystem + "\r\n\r\nАдрес\r\n" + node.Host + ":" + node.Port + "\r\n\r\nРоль\r\n" + (string.IsNullOrEmpty(node.Role) ? "Не указана" : node.Role);
            }
            else if (system != null)
            {
                _serverDetailTitle.Text = system.Name;
                _serverDetailBody.Text = "Узлов: " + system.Nodes.Count + "\r\n\r\nСервисы перед перезагрузкой:\r\n" + (system.Services.Count == 0 ? "Не настроены" : string.Join(", ", system.Services.ToArray()));
            }
            else
            {
                int total = 0, enabled = 0;
                foreach (SubSystem item in _cfg.Systems)
                    foreach (Node server in item.Nodes) { total++; if (server.Enabled) enabled++; }
                _serverDetailTitle.Text = "Инфраструктура";
                _serverDetailBody.Text = "Систем\r\n" + _cfg.Systems.Count + "\r\n\r\nСерверов\r\n" + total +
                    " (доступно для операций: " + enabled + ")\r\n\r\nВыберите систему или сервер слева для подробностей.";
            }
        }

        private Control BuildRecentReportsCard()
        {
            var card = new ModernCard { Dock = DockStyle.Top, Height = 190, BackColor = Theme.Surface, Padding = new Padding(14), Margin = new Padding(0, 12, 0, 0) };
            Theme.Box(card);
            var title = new Label { Dock = DockStyle.Top, Height = 26, Text = "Последние запуски", Font = Theme.UiFontBold };
            var list = new ModernListView { Dock = DockStyle.Fill, View = View.Details };
            list.Columns.Add("Каталог", 290); list.Columns.Add("Тип", 140); list.Columns.Add("Изменён", 160);
            try
            {
                Store.EnsureDirs();
                foreach (DirectoryInfo directory in new DirectoryInfo(Store.LogsDir).GetDirectories().OrderByDescending(d => d.LastWriteTime).Take(5))
                {
                    string kind = directory.Name.StartsWith("vuln_", StringComparison.OrdinalIgnoreCase) ? "ФСТЭК" :
                        directory.Name.StartsWith("preview_", StringComparison.OrdinalIgnoreCase) ? "Предпроверка" : "Операция";
                    var row = new ListViewItem(directory.Name) { Tag = directory.FullName };
                    row.SubItems.Add(kind); row.SubItems.Add(directory.LastWriteTime.ToString("dd.MM.yyyy HH:mm")); list.Items.Add(row);
                }
            }
            catch (Exception ex) { list.Items.Add(new ListViewItem("Не удалось прочитать историю: " + ex.Message)); }
            if (list.Items.Count == 0) list.Items.Add(new ListViewItem("История пока пуста"));
            list.DoubleClick += delegate
            {
                if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is string)
                    OpenPath((string)list.SelectedItems[0].Tag);
            };
            card.Controls.Add(list); card.Controls.Add(title);
            return card;
        }

        private void ShowShellMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            Theme.ContextMenu(menu);
            // ContextMenuStrip is still completing ItemClicked/keyboard dispatch
            // when Closed fires. Disposing it synchronously makes keyboard-picked
            // commands fail with ObjectDisposedException. Queue disposal after the
            // current Windows message has fully completed.
            menu.Closed += delegate
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(menu.Dispose));
                else menu.Dispose();
            };
            menu.Items.Add("SSH-ключи", null, delegate { ManageHostKeys(); });
            menu.Items.Add("Исключения пакетов", null, delegate { EditExclusions(); });
            menu.Items.Add("Обновить репозиторий", null, delegate { OpenRepo(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Экспорт конфигурации", null, delegate { DoExport(); });
            menu.Items.Add("Импорт конфигурации", null, delegate { DoImport(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("О программе", null, async delegate { if (AppDialog.About(this, AppUpdater.CurrentVersion)) await CheckAppUpdate(false); });
            menu.Items.Add(Theme.IsDark ? "Включить светлую тему" : "Включить тёмную тему", null, delegate { ToggleUiTheme(); });
            menu.Show(anchor, new Point(anchor.Width, 0));
        }

        private void ToggleUiTheme()
        {
            if (!CanEditConfiguration()) return;
            _cfg.UiTheme = Theme.IsDark ? "light" : "dark";
            Store.SaveConfig(_cfg);
            AppDialog.Info(this, "Оформление", "Тема сохранена. Приложение будет перезапущено для аккуратного применения ко всем окнам.");
            Application.Restart();
        }
    }
}
