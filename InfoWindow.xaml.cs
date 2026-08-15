using System.Windows;

namespace FinderWin;

public partial class InfoWindow : Window {
    public FileItem Item { get; }
    public string InfoTitle => $"“{Item.Name}” 简介";

    public InfoWindow(FileItem item) {
        Item = item;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => { ApplyIconState(); ApplyWindowClip(); };
        SizeChanged += (_, _) => ApplyWindowClip();
    }

    public string ItemName => Item.Name;
    public string FullPath => Item.FullPath;
    public string Kind => Item.Kind;
    public string SizeText => Item.SizeText;
    public string ExtensionLabel => Item.ExtensionLabel;
    public System.DateTime Created => Item.Created;
    public System.DateTime Modified => Item.Modified;

    private void ApplyIconState() {
        if (Item.IsDirectory) return;
        FolderPreview.Visibility = Visibility.Collapsed;
        LargeFolderPreview.Visibility = Visibility.Collapsed;
        if (Item.IsArchive) {
            ZipPreview.Visibility = Visibility.Visible;
            LargeZipPreview.Visibility = Visibility.Visible;
        } else {
            GenericPreview.Visibility = Visibility.Visible;
            LargeGenericPreview.Visibility = Visibility.Visible;
        }
    }

    private void ApplyWindowClip() {
        if (WindowShell.ActualWidth <= 0 || WindowShell.ActualHeight <= 0) return;
        WindowShell.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, WindowShell.ActualWidth, WindowShell.ActualHeight), 16, 16);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
