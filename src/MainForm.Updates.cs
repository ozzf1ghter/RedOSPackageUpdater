using System;
using System.Threading.Tasks;

namespace RedOSPackageUpdater
{
    public partial class MainForm
    {
        private bool _updateCheckRunning;

        private async Task CheckAppUpdate(bool silent)
        {
            if (_updateCheckRunning) return;
            _updateCheckRunning = true;
            UpdateProgressDialog updateProgress = null;
            try
            {
                if (!silent)
                {
                    SetStatus("Проверка версии...");
                    updateProgress = new UpdateProgressDialog();
                    updateProgress.SetStage("Проверка обновлений...", "Подключение к GitHub и проверка версии...");
                    updateProgress.Show(this);
                    updateProgress.Refresh();
                }
                UpdateInfo info = await Task.Run(() => AppUpdater.Check());
                if (updateProgress != null) { updateProgress.Close(); updateProgress.Dispose(); updateProgress = null; }
                if (!info.IsNewer)
                {
                    if (!silent) AppDialog.Info(this, "Обновления", "Установлена актуальная версия " + AppUpdater.CurrentVersion + ".");
                    return;
                }

                string message = "Доступна версия " + info.VersionText + ".\n\nСкачать и установить?";
                if (!AppDialog.Confirm(this, "Доступно обновление", message, "Обновить")) return;

                SetStatus("Скачивание обновления...");
                updateProgress = new UpdateProgressDialog();
                updateProgress.SetStage("Скачивание новой версии...", "Подключение к GitHub...");
                Enabled = false;
                updateProgress.Show(this);
                updateProgress.Refresh();
                var progressWindow = updateProgress;
                string downloaded = await Task.Run(() => AppUpdater.Download(info, (done, total) =>
                {
                    if (IsDisposed) return;
                    Ui(() => { if (!progressWindow.IsDisposed) progressWindow.SetProgress(done, total); });
                }));

                SetStatus("Установка обновления...");
                updateProgress.Close();
                updateProgress = null;
                Enabled = true;
                AppUpdater.InstallAndRestart(downloaded);
                Close();
            }
            catch (Exception ex)
            {
                if (updateProgress != null) { updateProgress.Dispose(); updateProgress = null; }
                if (!IsDisposed) Enabled = true;
                string message = UserError.Message(ex);
                if (!silent) AppDialog.Error(this, "Ошибка обновления", message);
                AppendLog("Ошибка проверки обновления: " + message);
            }
            finally
            {
                _updateCheckRunning = false;
                if (!IsDisposed && !Disposing)
                {
                    Enabled = true;
                    if (!_running) SetStatus("Готово");
                }
                if (updateProgress != null) updateProgress.Dispose();
            }
        }
    }
}
