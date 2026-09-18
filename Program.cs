namespace DesktopQuickAccess;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 多重起動防止
        using var mutex = new Mutex(true, "DesktopQuickAccess.SingleInstance.Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DesktopQuickAccess はすでに起動しています(タスクバー通知領域を確認してください)。",
                "DesktopQuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
