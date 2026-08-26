using System;

namespace RedOSPackageUpdater
{
    internal sealed class CommandBarLayout
    {
        public bool Compact;
        public bool ShortLabels;
        public int PreviewWidth, RunWidth, StopWidth;
        public int PreviewLeft, RunLeft, StopLeft, StatusLeft;
    }

    internal sealed class ServerWorkspaceLayout
    {
        public int SplitterDistance, LeftMinimum, RightMinimum;
    }

    // Чистая математика адаптивной вёрстки. Отделена от WinForms, чтобы минимальные
    // размеры и отсутствие наложений проверялись автоматически без запуска GUI.
    internal static class UiLayoutRules
    {
        public static CommandBarLayout CommandBar(int width, int statusWidth)
        {
            var result = new CommandBarLayout();
            // При меньшей ширине длинный итоговый статус скрывается из верхней
            // строки (полный текст остаётся в нижней строке состояния/tooltip),
            // чтобы не перекрывать кнопки запуска.
            // Внутренняя ширина страницы меньше ширины окна из-за навигации. При 1240px
            // окно даёт около 1026px рабочей области: полный режим здесь перекрывал сценарий.
            result.Compact = width < 1180;
            result.ShortLabels = width < 900;
            result.PreviewWidth = result.ShortLabels ? 116 : 164;
            result.RunWidth = result.ShortLabels ? 126 : 194;
            result.StopWidth = result.ShortLabels ? 90 : 124;
            int right = width - 12;
            result.StatusLeft = right - statusWidth;
            int actionRight = result.Compact ? right : result.StatusLeft - 8;
            result.StopLeft = actionRight - result.StopWidth;
            result.RunLeft = result.StopLeft - 8 - result.RunWidth;
            result.PreviewLeft = result.RunLeft - 8 - result.PreviewWidth;
            return result;
        }

        public static ServerWorkspaceLayout ServerWorkspace(int width, int splitterWidth)
        {
            int leftMin = width >= 900 ? 280 : 200;
            int rightMin = width >= 900 ? 420 : 280;
            int available = Math.Max(0, width - splitterWidth);
            if (leftMin + rightMin > available)
            {
                leftMin = Math.Max(160, (int)(available * 0.36));
                rightMin = Math.Max(180, available - leftMin - 20);
            }
            int maxDistance = Math.Max(leftMin, width - rightMin - splitterWidth);
            int desired = Math.Max(leftMin, Math.Min(maxDistance, width >= 900 ? 348 : (int)(width * 0.38)));
            return new ServerWorkspaceLayout
            {
                SplitterDistance = desired,
                LeftMinimum = Math.Min(leftMin, desired),
                RightMinimum = Math.Max(0, Math.Min(rightMin, width - desired - splitterWidth))
            };
        }
    }
}
