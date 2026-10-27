using System.Security.Principal;
using System.Windows.Forms;

namespace WinDefendOFF.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!IsElevated())
        {
            MessageBox.Show(
                "WinDefendOFF 需要系統管理員權限才能運作。\n請以系統管理員身分重新執行。",
                "權限不足",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
