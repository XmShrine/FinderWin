using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FinderWin;

public sealed class FileItem(
    string name,
    string fullPath,
    bool isDirectory,
    bool isHidden,
    bool hasChildren,
    long size,
    DateTime created,
    DateTime modified) : INotifyPropertyChanged {
    private double _canvasX;
    private double _canvasY;
    private string? _tagName;
    private string? _tagColor;
    private string _renameText = name;
    private bool _isRenaming;

    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
    public bool IsHidden { get; } = isHidden;
    public bool HasChildren { get; } = hasChildren;
    public long Size { get; } = size;
    public DateTime Created { get; } = created;
    public DateTime Modified { get; } = modified;
    public double CanvasX { get => _canvasX; set { if (Math.Abs(_canvasX - value) < .01) return; _canvasX = value; Notify(); } }
    public double CanvasY { get => _canvasY; set { if (Math.Abs(_canvasY - value) < .01) return; _canvasY = value; Notify(); } }
    public string Icon => IsDirectory ? "📁" : Extension.ToLowerInvariant() switch { ".zip" or ".rar" or ".7z" => "🗜", ".jpg" or ".jpeg" or ".png" => "▧", ".pdf" => "▤", ".mp3" or ".wav" => "♫", _ => "▤" };
    public string Kind => IsDirectory ? "文件夹" : string.IsNullOrEmpty(Extension) ? "文稿" : Extension[1..].ToUpperInvariant() + " 文稿";
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
    public string ExtensionLabel => IsDirectory ? "" : string.IsNullOrWhiteSpace(Extension) ? "FILE" : Extension.TrimStart('.').ToUpperInvariant();
    public double ExtensionLabelFontSize => ExtensionLabel.Length switch { <= 4 => 13, <= 7 => 10, _ => 8 };
    public bool IsArchive => !IsDirectory && Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    public string? TagName { get => _tagName; set { if (_tagName == value) return; _tagName = value; Notify(); Notify(nameof(HasTag)); } }
    public string? TagColor { get => _tagColor; set { if (_tagColor == value) return; _tagColor = value; Notify(); Notify(nameof(HasTag)); } }
    public bool HasTag => !string.IsNullOrWhiteSpace(TagName) && !string.IsNullOrWhiteSpace(TagColor);
    public string RenameText { get => _renameText; set { if (_renameText == value) return; _renameText = value; Notify(); } }
    public bool IsRenaming { get => _isRenaming; set { if (_isRenaming == value) return; _isRenaming = value; Notify(); } }
    public string SizeText => IsDirectory ? "--" : Size < 1024 ? $"{Size} B" : Size < 1024 * 1024 ? $"{Size / 1024d:F1} KB" : $"{Size / 1024d / 1024d:F1} MB";
    public bool IsMacMetadata => Name is ".DS_Store" or "__MACOSX";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
