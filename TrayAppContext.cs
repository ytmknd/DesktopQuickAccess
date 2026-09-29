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

    // 絞り込み中に下端(タスクバー側)を固定するかどうかを判定するときの許容誤差。
    private const int AnchorTolerance = 8;

    private readonly string _desktopPath;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ContextMenuStrip _managementMenu;
    private readonly ToolStripTextBox _searchBox;
    private readonly ToolStripSeparator _searchSeparator = new();
    private System.Windows.Forms.Timer? _hoverScrollTimer;

    // 検索ボックスをプログラムから書き換えるときに TextChanged による再構築を止めるためのフラグ。
    private bool _suppressSearchUpdate;

    // 絞り込み中に Enter で開く対象(先頭の候補)。
    private (string Path, bool IsDirectory)? _topMatch;

    private bool _keepBottomFixed;
    private int _anchorBottom;

    public TrayAppContext()
    {
        _desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        _searchBox = CreateSearchBox();

        // 左クリック: デスクトップの中身を表示するメニュー
        _menu = new SearchableMenu { CommandKeyHandler = HandleMenuCommandKey };
        _menu.Opening += (_, _) =>
        {
            ClearSearch();
            RebuildMenu();
        };
        _menu.Opened += (_, _) => OnMenuOpened();

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

    // ----- 絞り込み検索 -----------------------------------------------------

    private ToolStripTextBox CreateSearchBox()
    {
        var box = new ToolStripTextBox
        {
            Name = "searchBox",
            AutoSize = false,
            Width = 240,
            BorderStyle = BorderStyle.FixedSingle,
            ToolTipText = "名前で絞り込む(日本語入力可 / 空白区切りでAND検索)",
        };

        box.TextBox.PlaceholderText = "絞り込み...";
        box.TextChanged += (_, _) =>
        {
            if (_suppressSearchUpdate)
            {
                return;
            }

            RebuildMenu();
        };

        return box;
    }

    private void ClearSearch()
    {
        // TextChanged 経由の再構築を避ける(直後に呼び出し側が RebuildMenu するため)
        _suppressSearchUpdate = true;
        try
        {
            _searchBox.Text = string.Empty;
        }
        finally
        {
            _suppressSearchUpdate = false;
        }
    }

    private void OnMenuOpened()
    {
        // タスクバーに沿って上向きに開いた場合、絞り込みで高さが変わっても
        // 下端が動かないようにしたいので、開いた時点の位置を覚えておく。
        var workingArea = Screen.GetWorkingArea(_menu.Bounds);
        _keepBottomFixed = _menu.Bottom >= workingArea.Bottom - AnchorTolerance;
        _anchorBottom = _menu.Bottom;

        // 開いた直後からそのまま打ち込めるように、検索ボックスへフォーカスを移す。
        _menu.BeginInvoke(() => _searchBox.TextBox.Focus());
    }

    /// <summary>
    /// 検索ボックスに入力中のキー操作を処理する。true を返すとそのキーは消費される。
    ///
    /// ToolStripDropDown は Esc や Enter を ProcessDialogKey で先に消費してしまい、
    /// テキストボックスの KeyDown まで届かない。ProcessCmdKey は ProcessDialogKey より
    /// 前に呼ばれるため、<see cref="SearchableMenu"/> 経由でここにフックしている。
    ///
    /// IME で変換中の Enter / Esc は IME 側が消費するのでここには届かない。
    /// つまり日本語入力の確定・取り消し操作を邪魔しない。
    /// </summary>
    private bool HandleMenuCommandKey(Keys keyData)
    {
        // 一覧側にフォーカスが移っているときは通常のメニュー操作に任せる。
        if (!_searchBox.TextBox.Focused)
        {
            return false;
        }

        switch (keyData)
        {
            case Keys.Enter:
                if (_topMatch is not null)
                {
                    OpenTopMatch();
                    return true;
                }

                return SelectFirstResult();

            case Keys.Escape:
                // 1回目(入力あり)は入力のクリアだけ。
                if (_searchBox.TextBox.TextLength > 0)
                {
                    _searchBox.Text = string.Empty;
                    return true;
                }

                // 2回目(入力が空)は既定動作に任せてメニューを閉じる。
                return false;

            case Keys.Down:
            case Keys.Tab:
                return SelectFirstResult();

            default:
                return false;
        }
    }

    // 検索ボックスから下矢印/Tabで一覧に移動するため、最初の選択可能な項目を選ぶ。
    private bool SelectFirstResult()
    {
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item == _searchBox || item == _searchSeparator || !item.Enabled || !item.Available)
            {
                continue;
            }

            item.Select();

            // フォーカスがテキストボックスに残っていると以降の上下キーもここに来てしまい、
            // 一覧を移動できなくなる。メニュー本体にフォーカスを移して通常の
            // メニュー操作(上下キー/Enter/Esc)に引き継ぐ。
            _menu.Focus();
            return true;
        }

        return false;
    }

    private void OpenTopMatch()
    {
        if (_topMatch is not { } match)
        {
            return;
        }

        _menu.Close(ToolStripDropDownCloseReason.ItemClicked);

        if (match.IsDirectory)
        {
            OpenInExplorer(match.Path);
        }
        else
        {
            LaunchFile(match.Path);
        }
    }

    // ----- メニュー構築 -----------------------------------------------------

    private void RebuildMenu()
    {
        if (_menu.Items.Count == 0)
        {
            _menu.Items.Add(_searchBox);
            _menu.Items.Add(_searchSeparator);
        }

        // 検索ボックスはフォーカスを保ったまま使い回す必要があるため、
        // コレクション全体をクリアせずに結果部分(先頭2項目より後ろ)だけを差し替える。
        var stale = new List<ToolStripItem>();
        for (int i = _menu.Items.Count - 1; i >= 2; i--)
        {
            stale.Add(_menu.Items[i]);
            _menu.Items.RemoveAt(i);
        }

        _topMatch = null;
        _menu.SuspendLayout();
        try
        {
            PopulateItems(_menu.Items, _desktopPath, filter: SearchQuery.Create(_searchBox.Text));
        }
        finally
        {
            _menu.ResumeLayout(performLayout: true);
        }

        foreach (var item in stale)
        {
            item.Dispose();
        }

        RefreshMenuLayout();
    }

    // 表示中に項目数が変わるとAutoSizeにより高さが変わる。
    // 上向きに開いているときは下端が動かないよう位置を補正する。
    private void RefreshMenuLayout()
    {
        if (!_menu.Visible || !_keepBottomFixed)
        {
            return;
        }

        _menu.PerformLayout();

        if (_menu.Bottom != _anchorBottom)
        {
            _menu.Top = _anchorBottom - _menu.Height;
        }
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

    /// <param name="filter">
    /// null 以外を渡すと名前が一致する項目だけを表示する(先頭一致を部分一致より前に並べる)。
    /// サブメニューは絞り込みの対象外なので常に null で呼ぶ。
    /// </param>
    private void PopulateItems(ToolStripItemCollection collection, string folderPath, bool addOpenHeader = false,
        SearchQuery? filter = null)
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
            dirs = OrderForDisplay(Directory.EnumerateDirectories(folderPath), filter);
            files = OrderForDisplay(Directory.EnumerateFiles(folderPath), filter);
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

            if (filter is not null)
            {
                _topMatch ??= (dir, true);
            }
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

            if (filter is not null)
            {
                _topMatch ??= (file, false);
            }
        }

        if (truncated)
        {
            collection.Add(new ToolStripMenuItem("...(表示件数の上限に達しました)") { Enabled = false });
        }
        else if (count == 0)
        {
            collection.Add(new ToolStripMenuItem(filter is null ? "(空です)" : "(該当なし)") { Enabled = false });
        }
    }

    // 絞り込みなしなら名前順、絞り込み中は一致しない項目を落として
    // 先頭一致→部分一致の順に並べる。
    private static IEnumerable<string> OrderForDisplay(IEnumerable<string> paths, SearchQuery? filter)
    {
        if (filter is null)
        {
            return paths.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);
        }

        return paths
            .Select(path => (path, rank: filter.Match(Path.GetFileName(path))))
            .Where(entry => entry.rank != SearchQuery.NoMatch)
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => Path.GetFileName(entry.path), StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.path);
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

    /// <summary>
    /// 検索ボックス付きのメニュー。ToolStripDropDown の既定のキー処理より先に
    /// キーを受け取れるよう ProcessCmdKey をフックできるようにしたもの。
    /// </summary>
    private sealed class SearchableMenu : ContextMenuStrip
    {
        // デザイナでは使わないメニューなのでシリアル化の対象外にする(WFO1000 の回避)。
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<Keys, bool>? CommandKeyHandler { get; init; }

        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
            => CommandKeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref m, keyData);
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }
}
