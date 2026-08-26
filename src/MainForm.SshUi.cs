using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RedOSPackageUpdater
{
    /// <summary>Связь SSH-оркестратора с журналом и диалоги доверия host-key.</summary>
    public partial class MainForm
    {
        private SshOrchestrator NewOrchestrator(bool filteredLog)
        {
            var orchestrator = new SshOrchestrator(_cfg.Credentials, _cache);
            orchestrator.OnUnknownHostKey = ConfirmUnknownHostKey;
            if (filteredLog) orchestrator.OnLog = (host, line) => { if (Important(line)) BufferLog(host, line); };
            else orchestrator.OnLog = (host, line) => BufferLog(host, line);
            return orchestrator;
        }

        private bool ConfirmUnknownHostKey(string host, int port, string fingerprint)
        {
            if (_trustUnknownHostKeysForOperation) return true;
            bool accepted = false;
            Action ask = () =>
            {
                if (_trustUnknownHostKeysForOperation) { accepted = true; return; }
                string text = "SSH-ключ этого сервера ранее не был известен.\n\n" +
                    "Сервер: " + host + "\nПорт: " + port + "\nSHA-256: " + fingerprint +
                    "\n\nСверьте отпечаток с доверенным источником.";
                using (var dialog = new Form())
                {
                    dialog.Text = "Первое подключение к серверу";
                    dialog.Width = 590; dialog.Height = 300; dialog.StartPosition = FormStartPosition.CenterParent;
                    dialog.Font = Theme.UiFont; dialog.BackColor = Theme.Bg; dialog.ForeColor = Theme.Text;
                    dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.MaximizeBox = false; dialog.MinimizeBox = false;
                    var label = new Label { Left = 18, Top = 18, Width = 540, Height = 145, Text = text };
                    var trustBatch = new ModernCheckBox { Left = 18, Top = 170, Width = 540, Height = 38, BackColor = Theme.Surface,
                        Text = "Доверять остальным новым SSH-ключам только до завершения этой операции" };
                    var trust = new ModernButton { Text = "Доверять и сохранить", Left = 272, Top = 220, Width = 170, Height = 30, DialogResult = DialogResult.Yes };
                    var cancel = new ModernButton { Text = "Отмена", Left = 450, Top = 220, Width = 108, Height = 30, DialogResult = DialogResult.No };
                    Theme.Check(trustBatch); Theme.Primary(trust); Theme.Secondary(cancel);
                    dialog.Controls.Add(label); dialog.Controls.Add(trustBatch); dialog.Controls.Add(trust); dialog.Controls.Add(cancel);
                    dialog.AcceptButton = trust; dialog.CancelButton = cancel;
                    accepted = dialog.ShowDialog(this) == DialogResult.Yes;
                    if (accepted && trustBatch.Checked) _trustUnknownHostKeysForOperation = true;
                }
            };
            if (InvokeRequired) Invoke(ask); else ask();
            return accepted;
        }

        private void ManageHostKeys()
        {
            Dictionary<string, string> known = Store.LoadKnownHosts();
            using (var dialog = new Form())
            {
                dialog.Text = "Доверенные SSH-ключи серверов";
                dialog.Width = 760; dialog.Height = 440; dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Font = Theme.UiFont; dialog.BackColor = Theme.Bg; dialog.ForeColor = Theme.Text;
                var list = new ModernListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                    MultiSelect = true, HideSelection = false, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
                list.Columns.Add("Сервер и порт", 220);
                list.Columns.Add("Отпечаток SHA-256", 490);
                foreach (KeyValuePair<string, string> entry in known)
                {
                    var item = new ListViewItem(entry.Key);
                    item.SubItems.Add(entry.Value ?? "");
                    list.Items.Add(item);
                }
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
                var remove = new ModernButton { Text = "Удалить выбранные", Width = 160, Dock = DockStyle.Left };
                var close = new ModernButton { Text = "Закрыть", Width = 100, Dock = DockStyle.Right, DialogResult = DialogResult.OK };
                remove.Click += (sender, args) =>
                {
                    if (list.SelectedItems.Count == 0) return;
                    if (!AppDialog.Confirm(dialog, "Удаление доверенных SSH-ключей",
                        "При следующем подключении программа запросит подтверждение нового отпечатка выбранных серверов.", "Удалить")) return;
                    var selected = new List<ListViewItem>();
                    foreach (ListViewItem item in list.SelectedItems) selected.Add(item);
                    foreach (ListViewItem item in selected) { known.Remove(item.Text); list.Items.Remove(item); }
                    Store.SaveKnownHosts(known);
                };
                bottom.Controls.Add(remove); bottom.Controls.Add(close);
                dialog.Controls.Add(list); dialog.Controls.Add(bottom);
                Theme.Dialog(dialog);
                dialog.AcceptButton = close;
                dialog.ShowDialog(this);
            }
        }
    }
}
