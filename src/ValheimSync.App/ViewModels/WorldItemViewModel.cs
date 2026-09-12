using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimSync.Core.Models;

namespace ValheimSync.App.ViewModels;

public partial class WorldItemViewModel : ObservableObject
{
    /// <summary>Pixel size of the circular upload/download progress badge — matches the
    /// Width/Height set on its Ellipses in MainWindow.axaml.</summary>
    private const double BadgeSize = 18;

    public string Name { get; }
    public string SizeDisplay { get; }

    public event Action<WorldItemViewModel>? SelectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(IsUploading))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsTransferring))]
    private SyncStatus _status = SyncStatus.Unknown;

    /// <summary>0.0–1.0 fraction reported by SyncEngine while this world is
    /// uploading/downloading — drives <see cref="ProgressClipRect"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressClipRect))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockDisplay))]
    private string? _lockHolder;

    /// <summary>True when the current user holds this world's lock — drives the Done button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDone))]
    private bool _isLockedByMe;

    /// <summary>Done is only shown on the world the user has locked (pressed Play on).</summary>
    public bool ShowDone => IsLockedByMe;

    public WorldItemViewModel(string name, long sizeBytes)
    {
        Name = name;
        SizeDisplay = sizeBytes switch
        {
            > 1024 * 1024 => $"{sizeBytes / (1024.0 * 1024):F1} MB",
            > 1024 => $"{sizeBytes / 1024.0:F0} KB",
            _ => $"{sizeBytes} B"
        };
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

    public string StatusDisplay => Status switch
    {
        SyncStatus.InSync => "✓ In sync",
        SyncStatus.LocalNewer => "↑ Upload pending",
        SyncStatus.RemoteNewer => "↓ Update available",
        SyncStatus.Uploading => "Uploading...",
        SyncStatus.Downloading => "Downloading...",
        SyncStatus.LockedByOther => "🔒 In use",
        SyncStatus.Error => "⚠ Error",
        SyncStatus.LocalOnly => "Local only",
        SyncStatus.RemoteOnly => "Cloud only",
        _ => "—"
    };

    /// <summary>Drives the blue upload badge in the world list (in place of status text).</summary>
    public bool IsUploading => Status == SyncStatus.Uploading;

    /// <summary>Drives the green download badge in the world list (in place of status text).</summary>
    public bool IsDownloading => Status == SyncStatus.Downloading;

    public bool IsTransferring => IsUploading || IsDownloading;

    /// <summary>
    /// The rectangle that reveals the bottom <see cref="Progress"/> fraction of the
    /// transfer badge's colored circle (bound to a RectangleGeometry used as that circle's
    /// Clip in MainWindow.axaml) — so the badge visually fills from empty to full instead
    /// of just switching color.
    /// </summary>
    public Rect ProgressClipRect
    {
        get
        {
            var filled = BadgeSize * Math.Clamp(Progress, 0.0, 1.0);
            return new Rect(0, BadgeSize - filled, BadgeSize, filled);
        }
    }

    public string LockDisplay => LockHolder is null ? "" : $"🔒 {LockHolder}";
}
