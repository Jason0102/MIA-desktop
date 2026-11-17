using Mia.Services;

namespace Mia;

public sealed partial class App
{
    private InteractionDashboard? _dashboard;

    internal void OpenInteractionDashboard()
    {
        if (IsExiting) return;
        if (_dashboard is null)
        {
            var memory = InteractionMemory.Desktop() ??
                throw new InvalidOperationException("請先設定有效的桌面使用者綁定，再開啟後台資訊。");
            _dashboard = new InteractionDashboard(memory);
            Exit += (_, _) => _dashboard.Dispose();
        }
        _dashboard.Open();
    }
}
