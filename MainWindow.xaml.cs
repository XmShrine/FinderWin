using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace FinderWin;

public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged {
    private const int HotKeyId = 731, WmHotKey = 0x0312, ModShift = 0x0004, ModWin = 0x0008, VkOemPeriod = 0xBE;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hWnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private readonly ObservableCollection<FileItem> _files = [];
    private readonly List<string> _history = [];
    private ICollectionView _filesView;
    private CancellationTokenSource? _loadCancellation;
    private int _historyIndex = -1;
    private bool _showHidden;
    private bool _iconView;
    private FileItem? _canvasDragItem;
    private System.Windows.Point _canvasDragPointer;
    private bool _canvasDragging;
    private readonly List<string> _clipboardPaths = [];
    private readonly Dictionary<string, TagRecord> _tags;
    private static readonly (string Name, string Color)[] TagPalette = [("红色", "#FF5B57"), ("橙色", "#FF9F0A"), ("黄色", "#FFD60A"), ("绿色", "#30D158"), ("蓝色", "#0A84FF"), ("紫色", "#BF5AF2"), ("灰色", "#8E8E93")];
    private string? _activeTagFilter;
    private bool _clipboardIsCut;
    private readonly DispatcherTimer _layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _currentPath = "", _folderTitle = "Finder";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICollectionView FilesView => _filesView;
    public string CurrentPath { get => _currentPath; private set { _currentPath = value; Notify(nameof(CurrentPath)); } }
    public string FolderTitle { get => _folderTitle; private set { _folderTitle = value; Notify(nameof(FolderTitle)); } }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));

    public MainWindow() {
        InitializeComponent(); DataContext = this;
        _tags = LoadTags();
        _filesView = CollectionViewSource.GetDefaultView(_files);
        _filesView.Filter = FilterFile;
        _layoutSaveTimer.Tick += (_, _) => { _layoutSaveTimer.Stop(); SaveIconPositions(); };
        Loaded += async (_, _) => await NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Closed += (_, _) => { _layoutSaveTimer.Stop(); SaveIconPositions(); _loadCancellation?.Cancel(); var h = new WindowInteropHelper(this).Handle; if (h != 0) UnregisterHotKey(h, HotKeyId); };
        SizeChanged += (_, _) => { ApplyNativeRoundedRegion(); ApplyWindowClip(); };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        RegisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId, ModShift | ModWin, VkOemPeriod);
        ApplyNativeRoundedRegion();
    }
    private void ApplyNativeRoundedRegion() {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, 32, 32);
        if (region != 0) SetWindowRgn(handle, region, true); // Windows owns the region after this call.
    }
    private void ApplyWindowClip() {
        if (WindowShell.ActualWidth <= 0 || WindowShell.ActualHeight <= 0) return;
        WindowShell.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, WindowShell.ActualWidth, WindowShell.ActualHeight), 16, 16);
    }
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId) { ToggleHidden(); handled = true; } return 0;
    }

    private async Task NavigateAsync(string folder, bool addHistory = true) {
        if (!Directory.Exists(folder)) { StatusText.Text = "此位置不存在或不可用。"; return; }
        // Flush the current canvas before clearing it, including refresh and navigation.
        _layoutSaveTimer.Stop();
        if (_files.Count > 0 && !string.IsNullOrWhiteSpace(CurrentPath)) SaveIconPositions();
        _loadCancellation?.Cancel(); var cancellation = _loadCancellation = new();
        CurrentPath = folder; FolderTitle = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : folder;
        if (addHistory && (_historyIndex < 0 || _history[_historyIndex] != folder)) { _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1); _history.Add(folder); _historyIndex++; }
        StatusText.Text = "正在载入…"; _files.Clear();
        try {
            // This is the one and only place metadata is written: after a user opens a folder in FinderWin.
            var loaded = await Task.Run(() => LoadFolder(folder, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            foreach (var item in loaded) { ApplyStoredTag(item); _files.Add(item); }
            FileList.SelectedItem = null; IconList.SelectedItem = null; UpdateSelectionActions(); UpdateTagSidebar();
            _filesView.Refresh(); StatusText.Text = $"{_filesView.Cast<object>().Count()} 个项目" + (_showHidden ? " · 正在显示隐藏项目" : "");
        } catch (OperationCanceledException) { } catch (Exception error) { StatusText.Text = $"无法读取文件夹：{error.Message}"; }
    }

    private static List<FileItem> LoadFolder(string folder, CancellationToken token) {
        TouchMacMetadata(folder);
        var positions = ReadIconPositions(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        var index = 0;
        return Directory.EnumerateFileSystemEntries(folder, "*", options).Select(path => {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path); bool directory = (info.Attributes & FileAttributes.Directory) != 0;
            var name = Path.GetFileName(path);
            var position = positions.TryGetValue(name, out var saved) ? saved : new IconPosition(22 + (index % 6) * 142, 22 + (index / 6) * 150);
            index++;
            return new FileItem(name, path, directory, name.StartsWith('.') || name == "__MACOSX" || (info.Attributes & FileAttributes.Hidden) != 0, directory && HasChildren(path), directory ? 0 : info.Length, info.CreationTime, info.LastWriteTime) { CanvasX = position.X, CanvasY = position.Y };
        }).OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    private static bool HasChildren(string folder) { try { return Directory.EnumerateFileSystemEntries(folder).Take(1).Any(); } catch { return false; } }
    private bool FilterFile(object item) {
        if (item is not FileItem file || (!_showHidden && file.IsHidden)) return false;
        if (!string.IsNullOrWhiteSpace(_activeTagFilter) && !string.Equals(file.TagName, _activeTagFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;
        return file.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.ExtensionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (file.TagName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private sealed record TagRecord(string Name, string Color);
    private static string TagsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinderWin", "tags.json");
    private static Dictionary<string, TagRecord> LoadTags() {
        try {
            if (!File.Exists(TagsFile)) return new(StringComparer.OrdinalIgnoreCase);
            var stored = JsonSerializer.Deserialize<Dictionary<string, TagRecord>>(File.ReadAllText(TagsFile));
            return stored is null ? new(StringComparer.OrdinalIgnoreCase) : new(stored, StringComparer.OrdinalIgnoreCase);
        } catch (Exception error) { App.WriteLog("Could not read tags", error); return new(StringComparer.OrdinalIgnoreCase); }
    }
    private void SaveTags() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(TagsFile)!);
            var temporary = TagsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_tags));
            File.Move(temporary, TagsFile, true);
        } catch (Exception error) { App.WriteLog("Could not save tags", error); StatusText.Text = "无法保存标签"; }
    }
    private void ApplyStoredTag(FileItem item) {
        if (!_tags.TryGetValue(Path.GetFullPath(item.FullPath), out var tag)) return;
        item.TagName = tag.Name; item.TagColor = tag.Color;
    }
    private void AssignTag(IEnumerable<FileItem> items, string? name, string? color) {
        var changed = 0;
        foreach (var item in items) {
            var path = Path.GetFullPath(item.FullPath);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(color)) _tags.Remove(path);
            else _tags[path] = new TagRecord(name, color);
            item.TagName = name; item.TagColor = color; changed++;
        }
        SaveTags(); _filesView.Refresh();
        UpdateTagSidebar(); UpdateSelectionActions();
        StatusText.Text = string.IsNullOrWhiteSpace(name) ? $"已移除 {changed} 个项目的标签" : $"已给 {changed} 个项目添加{name}标签";
    }
    private void UpdateTagSidebar() {
        if (TagsSidebarPanel is null) return;
        TagsSidebarPanel.Children.Clear();
        var available = _files.Where(item => item.HasTag).Select(item => item.TagName!).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var tags = TagPalette.Concat(_tags.Values.Select(tag => (tag.Name, tag.Color)))
            .GroupBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).Select(group => group.First());
        foreach (var (name, color) in tags) {
            var dot = new System.Windows.Controls.Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!, VerticalAlignment = VerticalAlignment.Center };
            var label = new System.Windows.Controls.TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var content = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; content.Children.Add(dot); content.Children.Add(label);
            var button = new System.Windows.Controls.RadioButton { Style = (Style)FindResource("SidebarItem"), GroupName = "TagSidebar", Content = content, Tag = name, IsEnabled = available.Contains(name), IsChecked = string.Equals(_activeTagFilter, name, StringComparison.CurrentCultureIgnoreCase) };
            button.Click += TagSidebar_Click; TagsSidebarPanel.Children.Add(button);
        }
        if (!string.IsNullOrWhiteSpace(_activeTagFilter) && !available.Contains(_activeTagFilter)) { _activeTagFilter = null; _filesView.Refresh(); }
    }
    private void TagSidebar_Click(object sender, System.Windows.RoutedEventArgs e) {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string tag } button) return;
        if (string.Equals(_activeTagFilter, tag, StringComparison.CurrentCultureIgnoreCase)) { _activeTagFilter = null; button.IsChecked = false; }
        else _activeTagFilter = tag;
        FileList.UnselectAll(); IconList.UnselectAll(); _filesView.Refresh(); UpdateSelectionActions();
        StatusText.Text = _activeTagFilter is null ? $"{_filesView.Cast<object>().Count()} 个项目" : $"标签“{_activeTagFilter}”：{_filesView.Cast<object>().Count()} 个项目";
    }
    private static void TouchMacMetadata(string folder) {
        // Finder creates .DS_Store while browsing. __MACOSX belongs to ZIP archives
        // and is now created only by Compress_Click, never in the source folder.
        if (IsInsideMacArchiveMetadata(folder)) return;
        try {
            var store = Path.Combine(folder, ".DS_Store");
            if (!File.Exists(store)) WriteHiddenText(store, "{\"Positions\":{}}");
            File.SetAttributes(store, File.GetAttributes(store) | FileAttributes.Hidden);
        } catch (UnauthorizedAccessException) { } catch (IOException) { } // Network and read-only folders remain strictly read-only.
    }
    private static bool IsInsideMacArchiveMetadata(string folder) {
        var current = new DirectoryInfo(folder);
        while (current is not null) { if (string.Equals(current.Name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) return true; current = current.Parent; }
        return false;
    }
    private sealed record IconPosition(double X, double Y);
    private sealed record FinderState(Dictionary<string, IconPosition> Positions);
    private static string LayoutFileFor(string folder) {
        var normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinderWin", "Layouts", key + ".json");
    }
    private static Dictionary<string, IconPosition> ReadIconPositions(string folder) {
        if (IsInsideMacArchiveMetadata(folder)) return new(StringComparer.OrdinalIgnoreCase);
        try {
            var layoutFile = LayoutFileFor(folder);
            var source = File.Exists(layoutFile) ? layoutFile : Path.Combine(folder, ".DS_Store");
            var json = File.ReadAllText(source);
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') return new(StringComparer.OrdinalIgnoreCase);
            var positions = JsonSerializer.Deserialize<FinderState>(json)?.Positions;
            return positions is null ? new(StringComparer.OrdinalIgnoreCase) : new(positions, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) { App.WriteLog($"Could not read icon positions from {folder}", error); return new(StringComparer.OrdinalIgnoreCase); }
    }
    private bool SaveIconPositions() {
        if (string.IsNullOrEmpty(CurrentPath) || IsInsideMacArchiveMetadata(CurrentPath)) return false;
        var state = JsonSerializer.Serialize(new FinderState(_files.ToDictionary(item => item.Name, item => new IconPosition(item.CanvasX, item.CanvasY), StringComparer.OrdinalIgnoreCase)));
        var savedToDatabase = false;
        try {
            var layoutFile = LayoutFileFor(CurrentPath);
            Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
            var temporary = layoutFile + ".tmp";
            File.WriteAllText(temporary, state);
            File.Move(temporary, layoutFile, true);
            savedToDatabase = true;
        } catch (Exception error) { App.WriteLog($"Could not save FinderWin layout database for {CurrentPath}", error); }
        try {
            var store = Path.Combine(CurrentPath, ".DS_Store");
            WriteHiddenText(store, state);
            File.SetAttributes(store, File.GetAttributes(store) | FileAttributes.Hidden);
        } catch (UnauthorizedAccessException) { }
        catch (IOException error) { App.WriteLog($"Could not mirror icon positions to .DS_Store in {CurrentPath}", error); }
        if (!savedToDatabase) StatusText.Text = "无法保存图标位置";
        return savedToDatabase;
    }
    private void ScheduleIconPositionSave() { _layoutSaveTimer.Stop(); _layoutSaveTimer.Start(); }
    private static void WriteHiddenText(string path, string text) {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private void ToggleHidden() { _showHidden = !_showHidden; _filesView.Refresh(); StatusText.Text = _showHidden ? "正在显示隐藏项目" : "正在隐藏隐藏项目"; }
    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, System.Windows.RoutedEventArgs e) => WindowState = System.Windows.WindowState.Minimized;
    private void Zoom_Click(object sender, System.Windows.RoutedEventArgs e) => WindowState = WindowState == System.Windows.WindowState.Maximized ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized;
    private void ChangeView_Click(object sender, System.Windows.RoutedEventArgs e) {
        _iconView = !_iconView;
        FileList.Visibility = _iconView ? Visibility.Collapsed : Visibility.Visible;
        IconList.Visibility = _iconView ? Visibility.Visible : Visibility.Collapsed;
        ColumnsHeader.Visibility = _iconView ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionActions();
        StatusText.Text = _iconView ? "Finder 图标视图" : "Finder 列表视图";
    }
    private void Sort_Click(object sender, System.Windows.RoutedEventArgs e) {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (label, property) in new[] { ("按名称", "Name"), ("按修改日期", "Modified"), ("按大小", "Size"), ("按种类", "Kind") }) {
            var item = new System.Windows.Controls.MenuItem { Header = label };
            item.Click += (_, _) => { var view = (System.Windows.Data.ListCollectionView)_filesView; view.CustomSort = new FileItemComparer(property); view.Refresh(); StatusText.Text = $"已{label}"; };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as System.Windows.Controls.Button; menu.IsOpen = true;
    }
    private FileItem? ActiveItem => (_iconView ? IconList.SelectedItem : FileList.SelectedItem) as FileItem;
    private IEnumerable<FileItem> ActiveItems => (_iconView ? IconList.SelectedItems : FileList.SelectedItems).Cast<FileItem>();
    private void FileSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelectionActions();
    private void UpdateSelectionActions() {
        var enabled = ActiveItem is not null;
        if (ShareButton is not null) ShareButton.IsEnabled = enabled;
        if (TagButton is not null) TagButton.IsEnabled = enabled;
    }
    private void Share_Click(object sender, System.Windows.RoutedEventArgs e) {
        var selected = ActiveItems.ToArray(); if (selected.Length == 0) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var copyFiles = new System.Windows.Controls.MenuItem { Header = selected.Length == 1 ? "复制项目" : $"复制 {selected.Length} 个项目" };
        copyFiles.Click += (_, _) => {
            try {
                var paths = new System.Collections.Specialized.StringCollection(); paths.AddRange(selected.Select(item => item.FullPath).ToArray());
                var data = new System.Windows.DataObject(); data.SetFileDropList(paths); System.Windows.Clipboard.SetDataObject(data, true);
                StatusText.Text = "项目已复制，可粘贴到文件夹或支持的应用";
            } catch (Exception error) { StatusText.Text = $"无法复制项目：{error.Message}"; }
        };
        var copyPaths = new System.Windows.Controls.MenuItem { Header = "复制路径" };
        copyPaths.Click += (_, _) => { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(item => item.FullPath))); StatusText.Text = "已复制完整路径"; };
        var email = new System.Windows.Controls.MenuItem { Header = "通过邮件发送…" };
        email.Click += (_, _) => {
            var subject = selected.Length == 1 ? selected[0].Name : $"分享 {selected.Length} 个项目";
            var body = "项目位置：\r\n" + string.Join("\r\n", selected.Select(item => item.FullPath));
            try { Process.Start(new ProcessStartInfo($"mailto:?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}") { UseShellExecute = true }); }
            catch (Exception error) { StatusText.Text = $"无法打开邮件应用：{error.Message}"; }
        };
        var nearby = new System.Windows.Controls.MenuItem { Header = "附近共享设置…" };
        nearby.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:crossdevice") { UseShellExecute = true }); } catch (Exception error) { StatusText.Text = error.Message; } };
        menu.Items.Add(copyFiles); menu.Items.Add(copyPaths); menu.Items.Add(new System.Windows.Controls.Separator()); menu.Items.Add(email); menu.Items.Add(nearby);
        menu.PlacementTarget = sender as System.Windows.Controls.Button; menu.IsOpen = true;
    }
    private void Tag_Click(object sender, System.Windows.RoutedEventArgs e) {
        var selected = ActiveItems.ToArray(); if (selected.Length == 0) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var title = new System.Windows.Controls.MenuItem { Header = selected.Length == 1 ? $"将标签分配给“{selected[0].Name}”" : $"将标签分配给 {selected.Length} 个项目", IsEnabled = false };
        menu.Items.Add(title); menu.Items.Add(new System.Windows.Controls.Separator());
        foreach (var (name, color) in TagPalette) {
            var option = new System.Windows.Controls.MenuItem { Header = $"●  {name}", Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)! };
            option.Click += (_, _) => AssignTag(selected, name, color); menu.Items.Add(option);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var custom = new System.Windows.Controls.MenuItem { Header = "新建标签…" };
        custom.Click += (_, _) => { var name = Microsoft.VisualBasic.Interaction.InputBox("标签名称：", "新建标签", "工作").Trim(); if (name.Length > 0) AssignTag(selected, name, "#8E8E93"); };
        var remove = new System.Windows.Controls.MenuItem { Header = "移除标签", IsEnabled = selected.Any(item => item.HasTag) }; remove.Click += (_, _) => AssignTag(selected, null, null);
        menu.Items.Add(custom); menu.Items.Add(remove);
        menu.PlacementTarget = sender as System.Windows.Controls.Button; menu.IsOpen = true;
    }
    private async void More_Click(object sender, System.Windows.RoutedEventArgs e) {
        var menu = new System.Windows.Controls.ContextMenu();
        var refresh = new System.Windows.Controls.MenuItem { Header = "刷新" }; refresh.Click += async (_, _) => await NavigateAsync(CurrentPath, false);
        var hidden = new System.Windows.Controls.MenuItem { Header = _showHidden ? "隐藏隐藏项目" : "显示隐藏项目" }; hidden.Click += (_, _) => ToggleHidden();
        var reveal = new System.Windows.Controls.MenuItem { Header = "在 Windows 资源管理器中打开" }; reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CurrentPath}\"") { UseShellExecute = true });
        menu.Items.Add(refresh); menu.Items.Add(hidden); menu.Items.Add(new System.Windows.Controls.Separator()); menu.Items.Add(reveal); menu.PlacementTarget = sender as System.Windows.Controls.Button; menu.IsOpen = true;
        await Task.CompletedTask;
    }
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.OemPeriod) { ToggleHidden(); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F) { ExpandSearch(); e.Handled = true; }
    }
    private async void Back_Click(object sender, System.Windows.RoutedEventArgs e) { if (_historyIndex > 0) { _historyIndex--; await NavigateAsync(_history[_historyIndex], false); } }
    private async void Forward_Click(object sender, System.Windows.RoutedEventArgs e) { if (_historyIndex < _history.Count - 1) { _historyIndex++; await NavigateAsync(_history[_historyIndex], false); } }
    private async void Home_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private async void Recent_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "最近使用位置不可用");
    private async void Shared_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "共享文件夹不可用");
    private async void Applications_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "应用程序文件夹不可用");
    private async void Desktop_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    private async void Downloads_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    private async void Documents_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    private async void Pictures_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "图片文件夹不可用");
    private async void Movies_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "影片文件夹不可用");
    private async void Screenshots_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"), "尚未创建截屏文件夹");
    private async void ICloud_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateIfExistsAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive"), "未检测到 Windows 版 iCloud 云盘");
    private async Task NavigateIfExistsAsync(string path, string unavailableMessage) { if (Directory.Exists(path)) await NavigateAsync(path); else StatusText.Text = unavailableMessage; }
    private async void ChooseFolder_Click(object sender, System.Windows.RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { UseDescriptionForTitle = true, Description = "选择要在 Finder 中打开的文件夹" }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) await NavigateAsync(dialog.SelectedPath); }
    private void ExpandSearch() { SearchHost.Width = 210; SearchBox.Visibility = Visibility.Visible; SearchClearButton.Visibility = Visibility.Visible; SearchBox.Focus(); Keyboard.Focus(SearchBox); }
    private void CollapseSearch() { SearchBox.Clear(); SearchBox.Visibility = Visibility.Collapsed; SearchClearButton.Visibility = Visibility.Collapsed; SearchHost.Width = 38; Keyboard.ClearFocus(); }
    private void SearchToggle_Click(object sender, System.Windows.RoutedEventArgs e) { if (SearchBox.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(SearchBox.Text)) CollapseSearch(); else ExpandSearch(); }
    private void SearchClear_Click(object sender, System.Windows.RoutedEventArgs e) { if (string.IsNullOrEmpty(SearchBox.Text)) CollapseSearch(); else { SearchBox.Clear(); SearchBox.Focus(); } }
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { CollapseSearch(); e.Handled = true; } }
    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
        if (_filesView is null) return; _filesView.Refresh();
        var query = SearchBox.Text.Trim();
        StatusText.Text = string.IsNullOrEmpty(query) ? $"{_filesView.Cast<object>().Count()} 个项目" : $"找到 {_filesView.Cast<object>().Count()} 个匹配“{query}”的项目";
    }
    private async void FileList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => await OpenSelectionAsync();
    private async void IconList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) {
        if (IconList.SelectedItem is not FileItem item) return;
        if (item.IsDirectory) await NavigateAsync(item.FullPath); else Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
    }
    private void IconList_MouseLeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not System.Windows.Controls.ListBoxItem) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is System.Windows.Controls.ListBoxItem { DataContext: FileItem item }) {
            // Icon canvas intentionally uses single selection: assigning SelectedItem
            // clears the old visual selection without touching SelectedItems (which
            // WPF forbids in SelectionMode.Single).
            IconList.SelectedItem = item;
            _canvasDragItem = item; _canvasDragPointer = e.GetPosition(IconList); _canvasDragging = false;
        }
        else { IconList.SelectedItem = null; _canvasDragItem = null; _canvasDragging = false; }
    }
    private void IconList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
        if (_canvasDragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(IconList); var delta = point - _canvasDragPointer;
        if (!_canvasDragging && Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 3) return;
        _canvasDragging = true;
        _canvasDragItem.CanvasX = Math.Max(0, _canvasDragItem.CanvasX + delta.X);
        _canvasDragItem.CanvasY = Math.Max(0, _canvasDragItem.CanvasY + delta.Y);
        ScheduleIconPositionSave();
        _canvasDragPointer = point;
        e.Handled = true;
    }
    private void IconList_MouseLeftUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
        if (_canvasDragging) { _layoutSaveTimer.Stop(); if (SaveIconPositions()) StatusText.Text = "已将图标位置写入 .DS_Store"; }
        _canvasDragItem = null; _canvasDragging = false;
    }
    private async void IconList_Drop(object sender, System.Windows.DragEventArgs e) {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
        var destination = CurrentPath;
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not System.Windows.Controls.ListBoxItem) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.ListBoxItem { DataContext: FileItem { IsDirectory: true } target }) destination = target.FullPath;
        try { await Task.Run(() => { foreach (var source in paths) { var target = Path.Combine(destination, Path.GetFileName(source)); if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue; if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); } }); StatusText.Text = "已移动项目"; await NavigateAsync(CurrentPath, false); } catch (Exception error) { StatusText.Text = $"无法移动：{error.Message}"; }
    }
    private async void Open_Click(object sender, System.Windows.RoutedEventArgs e) => await OpenSelectionAsync();
    private async Task OpenSelectionAsync() { if (ActiveItem is not FileItem item) return; if (item.IsDirectory) await NavigateAsync(item.FullPath); else Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
    private async void NewFolder_Click(object sender, System.Windows.RoutedEventArgs e) { var name = Microsoft.VisualBasic.Interaction.InputBox("新建文件夹名称：", "新建文件夹", "未命名文件夹"); if (!string.IsNullOrWhiteSpace(name)) { try { Directory.CreateDirectory(Path.Combine(CurrentPath, name)); } catch (Exception error) { StatusText.Text = error.Message; } await NavigateAsync(CurrentPath, false); } }
    private async void Refresh_Click(object sender, System.Windows.RoutedEventArgs e) => await NavigateAsync(CurrentPath, false);
    private void ToggleHidden_Click(object sender, System.Windows.RoutedEventArgs e) => ToggleHidden();
    private async void Rename_Click(object sender, System.Windows.RoutedEventArgs e) { if (ActiveItem is not FileItem item) return; var name = Microsoft.VisualBasic.Interaction.InputBox("重命名：", "重命名", item.Name); if (!string.IsNullOrWhiteSpace(name) && name != item.Name && !name.Contains(Path.DirectorySeparatorChar) && !name.Contains(Path.AltDirectorySeparatorChar)) { try { if (item.IsDirectory) Directory.Move(item.FullPath, Path.Combine(CurrentPath, name)); else File.Move(item.FullPath, Path.Combine(CurrentPath, name)); } catch (Exception error) { StatusText.Text = error.Message; } await NavigateAsync(CurrentPath, false); } }
    private async void Trash_Click(object sender, System.Windows.RoutedEventArgs e) { foreach (FileItem item in ActiveItems.ToArray()) { try { if (item.IsDirectory) FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); else FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch (OperationCanceledException) { } } await NavigateAsync(CurrentPath, false); }
    private void Copy_Click(object sender, System.Windows.RoutedEventArgs e) { _clipboardPaths.Clear(); _clipboardPaths.AddRange(ActiveItems.Select(item => item.FullPath)); _clipboardIsCut = false; StatusText.Text = $"已复制 {_clipboardPaths.Count} 个项目"; }
    private void Cut_Click(object sender, System.Windows.RoutedEventArgs e) { _clipboardPaths.Clear(); _clipboardPaths.AddRange(ActiveItems.Select(item => item.FullPath)); _clipboardIsCut = true; StatusText.Text = $"已剪切 {_clipboardPaths.Count} 个项目"; }
    private async void Paste_Click(object sender, System.Windows.RoutedEventArgs e) {
        if (_clipboardPaths.Count == 0) { StatusText.Text = "剪贴板中没有可粘贴的项目"; return; }
        try {
            await Task.Run(() => {
                foreach (var source in _clipboardPaths.ToArray()) {
                    if (!File.Exists(source) && !Directory.Exists(source)) continue;
                    var destination = UniqueCopyPath(CurrentPath, Path.GetFileName(source));
                    if (_clipboardIsCut) { if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination); }
                    else if (Directory.Exists(source)) CopyDirectory(source, destination); else File.Copy(source, destination);
                }
            });
            StatusText.Text = "已粘贴项目"; if (_clipboardIsCut) _clipboardPaths.Clear(); await NavigateAsync(CurrentPath, false);
        } catch (Exception error) { StatusText.Text = $"无法粘贴：{error.Message}"; }
    }
    private async void Duplicate_Click(object sender, System.Windows.RoutedEventArgs e) {
        if (ActiveItem is not FileItem item) return;
        try { await Task.Run(() => { var destination = UniqueCopyPath(CurrentPath, item.Name, " 副本"); if (item.IsDirectory) CopyDirectory(item.FullPath, destination); else File.Copy(item.FullPath, destination); }); StatusText.Text = "已创建副本"; await NavigateAsync(CurrentPath, false); }
        catch (Exception error) { StatusText.Text = $"无法复制：{error.Message}"; }
    }
    private void GetInfo_Click(object sender, System.Windows.RoutedEventArgs e) { if (ActiveItem is FileItem item) new InfoWindow(item) { Owner = this }.Show(); }
    private async void Compress_Click(object sender, System.Windows.RoutedEventArgs e) {
        var selected = ActiveItems.Where(item => !item.IsMacMetadata).ToArray();
        if (selected.Length == 0) { StatusText.Text = "请选择要压缩的项目"; return; }
        var baseName = selected.Length == 1 ? selected[0].Name : "归档";
        var archivePath = UniqueCopyPath(CurrentPath, baseName + ".zip");
        try {
            await Task.Run(() => {
                using (var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, false, new Utf8BytesWithoutFlagEncoding())) {
                    foreach (var item in selected) {
                        if (item.IsDirectory) AddDirectoryToArchive(archive, item.FullPath, MacZipName(item.Name));
                        else archive.CreateEntryFromFile(item.FullPath, MacZipName(item.Name), CompressionLevel.Optimal);
                    }
                    archive.CreateEntry("__MACOSX/");
                    var metadata = archive.CreateEntry("__MACOSX/.DS_Store", CompressionLevel.Fastest);
                    using var writer = new StreamWriter(metadata.Open(), Encoding.UTF8); writer.Write("FinderWin macOS archive metadata");
                }
            });
            StatusText.Text = $"已创建 {Path.GetFileName(archivePath)}"; await NavigateAsync(CurrentPath, false);
        } catch (Exception error) { StatusText.Text = $"压缩失败：{error.Message}"; }
    }
    private async void Extract_Click(object sender, System.Windows.RoutedEventArgs e) {
        if (ActiveItem is not FileItem { IsArchive: true } item) { StatusText.Text = "请选择 ZIP 归档"; return; }
        var destination = UniqueCopyPath(CurrentPath, Path.GetFileNameWithoutExtension(item.Name));
        try { await Task.Run(() => ExtractMacArchive(item.FullPath, destination)); StatusText.Text = $"已解压到 {Path.GetFileName(destination)}"; await NavigateAsync(CurrentPath, false); }
        catch (Exception error) { StatusText.Text = $"解压失败：{error.Message}"; }
    }
    private static void ExtractMacArchive(string archivePath, string destination) {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, new Utf8BytesWithoutFlagEncoding());
        foreach (var entry in archive.Entries) {
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (normalizedName.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) || normalizedName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("归档包含不安全的路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }
    private static void AddDirectoryToArchive(ZipArchive archive, string directory, string entryRoot) {
        var files = Directory.EnumerateFiles(directory).ToArray(); var folders = Directory.EnumerateDirectories(directory).Where(path => !string.Equals(Path.GetFileName(path), "__MACOSX", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0 && folders.Length == 0) archive.CreateEntry(entryRoot.TrimEnd('/') + "/");
        foreach (var file in files.Where(path => !string.Equals(Path.GetFileName(path), ".DS_Store", StringComparison.OrdinalIgnoreCase))) archive.CreateEntryFromFile(file, entryRoot.TrimEnd('/') + "/" + MacZipName(Path.GetFileName(file)), CompressionLevel.Optimal);
        foreach (var folder in folders) AddDirectoryToArchive(archive, folder, entryRoot.TrimEnd('/') + "/" + MacZipName(Path.GetFileName(folder)));
    }
    private static string MacZipName(string name) {
        return name.Normalize(NormalizationForm.FormD);
    }
    private sealed class Utf8BytesWithoutFlagEncoding : Encoding {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public override int GetByteCount(char[] chars, int index, int count) => Utf8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => Utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => Utf8.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => Utf8.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => Utf8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => Utf8.GetMaxCharCount(byteCount);
    }
    private void QuickLook_Click(object sender, System.Windows.RoutedEventArgs e) {
        if (ActiveItem is not FileItem item) return;
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); } catch (Exception error) { StatusText.Text = error.Message; }
    }
    private static string UniqueCopyPath(string destinationFolder, string name, string suffix = "") {
        var extension = Path.GetExtension(name); var stem = string.IsNullOrEmpty(extension) ? name : Path.GetFileNameWithoutExtension(name);
        var candidate = Path.Combine(destinationFolder, stem + suffix + extension); var number = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate)) { candidate = Path.Combine(destinationFolder, $"{stem}{suffix} {number}{extension}"); number++; }
        return candidate;
    }
    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(source)) CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
    private void FileList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e) { }

    private sealed class FileItemComparer(string property) : System.Collections.IComparer {
        public int Compare(object? x, object? y) {
            if (x is not FileItem a || y is not FileItem b) return 0;
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return property switch { "Modified" => b.Modified.CompareTo(a.Modified), "Size" => b.Size.CompareTo(a.Size), "Kind" => string.Compare(a.Kind, b.Kind, StringComparison.CurrentCultureIgnoreCase), _ => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) };
        }
    }
}
