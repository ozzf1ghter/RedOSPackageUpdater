using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    public partial class MainForm
    {
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
                _treeEmpty.Text = query.Length > 0 ? "Ничего не найдено\r\n\r\nИзмените поисковый запрос" : "Серверов пока нет\r\n\r\nДобавьте группу серверов, затем первый сервер";
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
                m.Items.Add("Проверить изменения на сервере", null, (s, e) => RunPreviewTargets(CollectSingle(node)));
                m.Items.Add("Запустить на этом сервере", null, (s, e) => RunTargets(CollectSingle(node)));
                m.Items.Add("Изменить", null, (s, e) => EditSelected());
                m.Items.Add("Удалить", null, (s, e) => DeleteSelected());
            }
            else if (node.Tag is SubSystem)
            {
                m.Items.Add("Проверить изменения в группе", null, (s, e) => RunPreviewTargets(CollectSystem(node)));
                m.Items.Add("Запустить на всей группе", null, (s, e) => RunTargets(CollectSystem(node)));
                m.Items.Add("Добавить сервер", null, (s, e) => { _tree.SelectedNode = node; AddNode(); });
                m.Items.Add("Добавить несколько серверов", null, (s, e) => { _tree.SelectedNode = node; BulkNodes(); });
                m.Items.Add("Службы перед перезагрузкой", null, (s, e) => { _tree.SelectedNode = node; EditServices(); });
                m.Items.Add("Переименовать", null, (s, e) => RenameSystem(node));
                m.Items.Add("Удалить группу", null, (s, e) => DeleteSelected());
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
            string name = Prompt.Show("Новая группа серверов", "Название группы:", "", false, new Size(360, 130));
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
            if (sys == null) { AppDialog.Info(this, "Не выбрана группа", "Сначала выберите группу серверов слева."); return; }
            using (var f = new NodeForm(null))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    if (!EnsureUniqueHost(f.Result, null)) return;
                    sys.Nodes.Add(f.Result); Store.SaveConfig(_cfg); RebuildTree();
                }
        }
        private void BulkNodes()
        {
            if (!CanEditConfiguration()) return;
            var sys = CurrentSystem();
            if (sys == null) { AppDialog.Info(this, "Не выбрана группа", "Сначала выберите группу серверов слева."); return; }
            using (var f = new BulkNodesForm())
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null && f.Result.Count > 0)
                {
                    var accepted = new List<Node>();
                    int duplicates = 0;
                    foreach (Node candidate in f.Result)
                    {
                        if (FindNodeByHost(candidate.Host, null) != null || accepted.Any(n => string.Equals(n.Host, candidate.Host, StringComparison.OrdinalIgnoreCase))) duplicates++;
                        else accepted.Add(candidate);
                    }
                    if (accepted.Count == 0)
                    {
                        AppDialog.Info(this, "Серверы не добавлены", "Все введённые адреса уже существуют в конфигурации.");
                        return;
                    }
                    sys.Nodes.AddRange(accepted);
                    Store.SaveConfig(_cfg); RebuildTree();
                    SetStatus("Добавлено серверов: " + accepted.Count + (duplicates > 0 ? "; повторов пропущено: " + duplicates : ""));
                    if (duplicates > 0) AppDialog.Info(this, "Повторяющиеся адреса пропущены", "Добавлено серверов: " + accepted.Count + ". Уже существующих или повторных адресов: " + duplicates + ".");
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
                        if (!EnsureUniqueHost(f.Result, node)) return;
                        node.Name = f.Result.Name; node.Host = f.Result.Host; node.Port = f.Result.Port;
                        node.Role = f.Result.Role; node.Enabled = f.Result.Enabled;
                        Store.SaveConfig(_cfg); RebuildTree();
                    }
            }
            else if (n.Tag is SubSystem) RenameSystem(n);
        }

        private Node FindNodeByHost(string host, Node ignore)
        {
            string value = (host ?? "").Trim();
            foreach (SubSystem system in _cfg.Systems)
                foreach (Node node in system.Nodes)
                    if (!ReferenceEquals(node, ignore) && string.Equals((node.Host ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase)) return node;
            return null;
        }

        private bool EnsureUniqueHost(Node candidate, Node ignore)
        {
            Node duplicate = FindNodeByHost(candidate == null ? null : candidate.Host, ignore);
            if (duplicate == null) return true;
            AppDialog.Info(this, "Адрес уже используется",
                "Сервер с адресом «" + candidate.Host + "» уже есть в конфигурации: " + HostIdentity.Label(duplicate.Name, duplicate.Host) + ".\r\n\r\nОдин адрес нельзя запускать параллельно из нескольких записей.");
            return false;
        }
        private void DeleteSelected()
        {
            if (!CanEditConfiguration()) return;
            var n = _tree.SelectedNode; if (n == null) return;
            if (n.Tag is Node)
            {
                var sys = CurrentSystem();
                if (sys != null && AppDialog.Confirm(this, "Удаление сервера", "Удалить запись сервера " + HostIdentity.Label(((Node)n.Tag).Name, ((Node)n.Tag).Host) + " из программы? Данные на сервере затронуты не будут.", "Удалить запись"))
                { sys.Nodes.Remove((Node)n.Tag); Store.SaveConfig(_cfg); RebuildTree(); }
            }
            else if (n.Tag is SubSystem)
            {
                if (AppDialog.Confirm(this, "Удаление группы серверов", "Удалить группу «" + ((SubSystem)n.Tag).Name + "» и все записи серверов в ней? Серверы и данные на них затронуты не будут.", "Удалить группу"))
                { _cfg.Systems.Remove((SubSystem)n.Tag); Store.SaveConfig(_cfg); RebuildTree(); }
            }
        }
        private void EditServices()
        {
            if (!CanEditConfiguration()) return;
            var sys = CurrentSystem();
            if (sys == null) { AppDialog.Info(this, "Не выбрана группа", "Сначала выберите группу серверов слева."); return; }
            string cur = string.Join(Environment.NewLine, sys.Services.ToArray());
            string txt = Prompt.Show("Службы перед перезагрузкой",
                "Эти службы будут остановлены перед перезагрузкой и проверены после запуска.\r\n\r\nУкажите имена или маски systemd — по одной на строку, например postgresql* или patroni:",
                cur, true, new Size(520, 350));
            if (txt == null) return;
            List<string> services;
            string validationError;
            if (!OperationDomain.TryNormalizeServiceMasks(txt.Replace("\r", "").Split('\n'), out services, out validationError))
            {
                AppDialog.Info(this, "Проверьте список служб", validationError);
                return;
            }
            sys.Services = services;
            Store.SaveConfig(_cfg);
            SetStatus("Список служб перед перезагрузкой обновлён");
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

        private void ClearCredentialCache()
        {
            if (!CanEditConfiguration()) return;
            if (_cache == null || _cache.Count == 0)
            {
                AppDialog.Info(this, "Кэш подключений пуст", "Сохранённых соответствий серверов и учётных записей нет.");
                return;
            }
            if (!AppDialog.Confirm(this, "Сброс кэша подключений",
                "Программа забудет, какая учётная запись подошла каждому серверу. Сами учётные записи и пароли удалены не будут.\r\n\r\nПри следующем подключении программа снова переберёт доступные учётные записи.", "Сбросить")) return;
            _cache.Clear();
            Store.SaveCache(_cache);
            SetStatus("Кэш подключений сброшен");
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
            if (!n.Enabled) { AppDialog.Info(this, "Сервер отключён", "Сначала включите сервер в его свойствах."); return list; }
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
            if (!HasUsableCredentials())
            {
                AppDialog.Info(this, "Нет доступных учётных записей",
                    "Добавьте учётную запись в разделе «Доступ и SSH». Если конфигурация перенесена с другого компьютера, локальные DPAPI-пароли нужно ввести заново.");
                return false;
            }
            if (targets == null || targets.Count == 0)
            {
                // Вызывающий код из контекстного меню дерева ("Запустить всю систему" на системе без
                // включённых узлов) раньше полагался только на эту проверку без своего MessageBox -
                // пункт меню молча "не срабатывал", пользователь не понимал, что произошло.
                AppDialog.Info(this, "Нет доступных серверов", "Для запуска нет включённых серверов.");
                return false;
            }
            var invalid = targets.Where(target => target == null || target.Node == null || string.IsNullOrWhiteSpace(target.Node.Host) ||
                target.Node.Port < 1 || target.Node.Port > 65535).ToList();
            if (invalid.Count > 0)
            {
                AppDialog.Error(this, "Некорректные серверы",
                    "Операция не запущена: у " + invalid.Count + " выбранных записей отсутствует адрес или указан недопустимый SSH-порт. Исправьте записи на странице «Серверы».");
                return false;
            }
            foreach (SubSystem system in targets.Where(target => target.System != null).Select(target => target.System).Distinct())
            {
                List<string> normalizedServices;
                string serviceError;
                if (!OperationDomain.TryNormalizeServiceMasks(system.Services, out normalizedServices, out serviceError))
                {
                    AppDialog.Error(this, "Некорректные службы перед перезагрузкой",
                        "Операция не запущена. В группе «" + system.Name + "» сохранена небезопасная или некорректная маска.\r\n\r\n" + serviceError +
                        "\r\n\r\nИсправьте список на странице «Серверы».");
                    return false;
                }
            }
            return true;
        }

        private bool HasUsableCredentials()
        {
            return _cfg.Credentials.Any(credential => credential != null &&
                !string.IsNullOrWhiteSpace(credential.User) && credential.Password != null);
        }

        private string ExcludeMasks()
        {
            return (_cfg.ExcludePackages != null) ? string.Join(" ", _cfg.ExcludePackages.ToArray()) : "";
        }

        // Папка логов конкретного запуска: Store.LogsDir\<prefix><timestamp>. Раньше эта строка была
        // продублирована по месту в 4 разных методах (RunTargets/RunRepoTargets/RunPkgOpTargets/
        // RunPreviewTargets), каждый со своим Path.Combine и своим форматом даты.
        private static string NewLogDir(string prefix)
        {
            return OperationDomain.NewLogDirectory(Store.LogsDir, prefix, DateTime.Now);
        }

        // "Готово. OK: N  WARN: N  FAIL: N" - одинаковый подсчёт и текст статуса после RunTargets и
        // RunPkgOpTargets (раньше дублировался дословно в обоих местах).
        private void ReportBatchStatus(List<HostResult> res)
        {
            SetStatus(OperationDomain.CountResults(res).StatusText);
        }

        private string SelectedProfileResource()
        {
            return SelectedScenario.ProfileResource ?? Profiles.KernelSecurity;
        }

        // Ключ профиля для скрипта предпроверки (чтобы dry-run считал ту же транзакцию, что и боевой прогон).
        private string SelectedProfileKey()
        {
            return SelectedScenario.ProfileKey ?? "kernel_security";
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

    }
}
