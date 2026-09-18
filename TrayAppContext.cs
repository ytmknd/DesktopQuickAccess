using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace DesktopQuickAccess;

/// <summary>
/// タスクバー通知領域に常駐し、クリックでデスクトップの中身を一覧表示する。
/// Windows 10 の「タスクバーツールバー」機能を Windows 11 で代替するためのユーティリティ。
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const int MaxItemsPerFolder = 300;

    private readonly string _desktopPath;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ContextMenuStrip _managementMenu;
    private System.Windows.Forms.Timer? _hoverScrollTimer;

    public TrayAppContext()
    {
        _desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // 左クリック: デスクトップの中身を表示するメニュー
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        // 右クリック: アプリの管理メニュー(スタートアップ登録/終了)
        _managementMenu = new ContextMenuStrip();
        _managementMenu.Opening += (_, _) => RebuildManagementMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "デスクトップ クイックアクセス",
            Visible = true,
            ContextMenuStrip = _managementMenu,
        };
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.DoubleClick += (_, _) => OpenInExplorer(_desktopPath);

        // 閉じたら右クリック用メニューに戻す(既定でNotifyIconに割り当てるのは右クリック用のため)
        _menu.Closed += (_, _) => _notifyIcon.ContextMenuStrip = _managementMenu;

        HookHoverScroll();
        PromoteTrayIconVisibility();
        WarmUpMenu();
    }

    // 初回クリック時にJITコンパイルやシェルアイコン取得が重なって
    // メニュー表示が遅く感じられるため、起動直後にバックグラウンドで
    // 一度メニューを構築しておき、実際のクリック時は速く表示されるようにする。
    private void WarmUpMenu()
    {
        var warmUpTimer = new System.Windows.Forms.Timer { Interval = 300 };
        warmUpTimer.Tick += (_, _) =>
        {
            warmUpTimer.Stop();
            warmUpTimer.Dispose();
            try
            {
                RebuildMenu();
            }
            catch
            {
                // ウォームアップの失敗は無視して構わない(実際のクリック時に再構築される)
            }
        };
        warmUpTimer.Start();
    }

    // ToolStripDropDownMenu の上下スクロールボタンは既定では「押し続け」でしかスクロールしないため、
    // 内部APIを使ってカーソルを乗せただけで自動スクロールするようにする。
    private void HookHoverScroll()
    {
        var menuType = typeof(ToolStripDropDownMenu);
        var scrollMethod = menuType.GetMethod("ScrollInternal", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(bool)], modifiers: null);
        var updateStatusMethod = menuType.GetMethod("UpdateScrollButtonStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        if (scrollMethod is null || updateStatusMethod is null)
        {
            return;
        }

        HookScrollButton(menuType.GetProperty("UpScrollButton", BindingFlags.Instance | BindingFlags.NonPublic), scrollMethod, updateStatusMethod, up: true);
        HookScrollButton(menuType.GetProperty("DownScrollButton", BindingFlags.Instance | BindingFlags.NonPublic), scrollMethod, updateStatusMethod, up: false);
    }

    private void HookScrollButton(PropertyInfo? property, MethodInfo scrollMethod, MethodInfo updateStatusMethod, bool up)
    {
        if (property?.GetValue(_menu) is not ToolStripItem button)
        {
            return;
        }

        button.MouseEnter += (_, _) =>
        {
            StopHoverScroll();
            _hoverScrollTimer = new System.Windows.Forms.Timer { Interval = 80 };
            _hoverScrollTimer.Tick += (_, _) =>
            {
                try
                {
                    // これ以上スクロールできない端まで来たらボタンがEnabled=falseになるので、
                    // 端で止めて余白ができないようにする。
                    if (!button.Enabled)
                    {
                        StopHoverScroll();
                        return;
                    }

                    scrollMethod.Invoke(_menu, [up]);
                    updateStatusMethod.Invoke(_menu, null);

                    if (!button.Enabled)
                    {
                        StopHoverScroll();
                    }
                }
                catch
                {
                    StopHoverScroll();
                }
            };
            _hoverScrollTimer.Start();
        };
        button.MouseLeave += (_, _) => StopHoverScroll();
    }

    private void StopHoverScroll()
    {
        _hoverScrollTimer?.Stop();
        _hoverScrollTimer?.Dispose();
        _hoverScrollTimer = null;
    }

    // Windows は新規アプリのトレイアイコンを既定で「非表示のアイコン」欄に隠す。
    // OneDrive等と同様に常時表示させたいので、シェルが管理する
    // NotifyIconSettings レジストリに直接 IsPromoted=1 を書き込む。
    // これは非公開の内部仕様のため、失敗しても致命的にならないようベストエフォートで行う。
    private void PromoteTrayIconVisibility()
    {
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var root = Registry.CurrentUser.OpenSubKey(
                        @"Control Panel\NotifyIconSettings", writable: true);

                    if (root is not null)
                    {
                        foreach (var subKeyName in root.GetSubKeyNames())
                        {
                            using var sub = root.OpenSubKey(subKeyName, writable: true);
                            if (sub?.GetValue("ExecutablePath") is string path &&
                                string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                            {
                                if (sub.GetValue("IsPromoted") is not int promoted || promoted != 1)
                                {
                                    sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                }
                                return;
                            }
                        }
                    }
                }
                catch
                {
                    return;
                }

                await Task.Delay(500);
            }
        });
    }

    // 左クリック: デスクトップの中身を表示するメニューを開く。
    // NotifyIcon内部のShowContextMenuを使うことで、右クリック時と同様に
    // フォーカス制御まで含めて正しく処理される(外側クリックで閉じる、等)。
    // 右クリックは既定の動作(_managementMenuがContextMenuStripに割り当て済み)に任せる。
    private void NotifyIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _notifyIcon.ContextMenuStrip = _menu;
        typeof(NotifyIcon)
            .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(_notifyIcon, null);
    }

    // exe自体に埋め込まれたアイコン(ApplicationIcon)をトレイアイコンとしても利用する。
    // 単一exe化した場合でもネイティブPEリソースとして残るため、この方法で取得できる。
    private static Icon LoadAppIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void RebuildMenu()
    {
        DisposeItems(_menu.Items);

        PopulateItems(_menu.Items, _desktopPath);
    }

    private void RebuildManagementMenu()
    {
        DisposeItems(_managementMenu.Items);

        var openDesktop = new ToolStripMenuItem("デスクトップを開く", null, (_, _) => OpenInExplorer(_desktopPath))
        {
            Font = new Font(_managementMenu.Font, FontStyle.Bold),
        };
        _managementMenu.Items.Add(openDesktop);
        _managementMenu.Items.Add(new ToolStripSeparator());

        bool autoStart = StartupManager.IsEnabled();
        _managementMenu.Items.Add(new ToolStripMenuItem(
            autoStart ? "スタートアップ登録を解除する" : "スタートアップに登録する",
            null,
            (_, _) =>
            {
                if (StartupManager.IsEnabled())
                {
                    StartupManager.Disable();
                }
                else
                {
                    StartupManager.Enable();
                }
            }));

        _managementMenu.Items.Add(new ToolStripSeparator());
        _managementMenu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => ExitApp()));
    }

    private void PopulateItems(ToolStripItemCollection collection, string folderPath, bool addOpenHeader = false)
    {
        if (addOpenHeader)
        {
            collection.Add(new ToolStripMenuItem("このフォルダーを開く", null, (_, _) => OpenInExplorer(folderPath))
            {
                Font = new Font(_menu.Font, FontStyle.Bold),
            });
            collection.Add(new ToolStripSeparator());
        }

        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(folderPath)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);
            files = Directory.EnumerateFiles(folderPath)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            collection.Add(new ToolStripMenuItem("(アクセスできません)") { Enabled = false });
            return;
        }

        int count = 0;
        bool truncated = false;

        foreach (var dir in dirs)
        {
            if (++count > MaxItemsPerFolder) { truncated = true; break; }

            var item = new ToolStripMenuItem(Path.GetFileName(dir))
            {
                Image = ShellIcon.GetIcon(dir, isDirectory: true),
            };
            // 開いた瞬間に中身を再取得する(常に最新の状態を反映)
            item.DropDownItems.Add(new ToolStripMenuItem("読み込み中..."));
            item.DropDownOpening += (_, _) =>
            {
                DisposeItems(item.DropDownItems);
                PopulateItems(item.DropDownItems, dir, addOpenHeader: true);
            };
            collection.Add(item);
        }

        foreach (var file in files)
        {
            if (++count > MaxItemsPerFolder) { truncated = true; break; }

            var item = new ToolStripMenuItem(Path.GetFileName(file))
            {
                Image = ShellIcon.GetIcon(file, isDirectory: false),
            };
            item.Click += (_, _) => LaunchFile(file);
            collection.Add(item);
        }

        if (truncated)
        {
            collection.Add(new ToolStripMenuItem("...(表示件数の上限に達しました)") { Enabled = false });
        }
        else if (count == 0)
        {
            collection.Add(new ToolStripMenuItem("(空です)") { Enabled = false });
        }
    }

    // Dispose中にコレクションから自身を取り除こうとするため、
    // 先にスナップショットを取ってからクリア・破棄する。
    // Imageはキャッシュ(ShellIcon)が保持する共有インスタンスのため、ここでは破棄しない。
    private static void DisposeItems(ToolStripItemCollection items)
    {
        var snapshot = new ToolStripItem[items.Count];
        items.CopyTo(snapshot, 0);
        items.Clear();

        foreach (var item in snapshot)
        {
            item.Dispose();
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"開けませんでした:\n{path}\n\n{ex.Message}", "DesktopQuickAccess",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void LaunchFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"開けませんでした:\n{path}\n\n{ex.Message}", "DesktopQuickAccess",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }
}
