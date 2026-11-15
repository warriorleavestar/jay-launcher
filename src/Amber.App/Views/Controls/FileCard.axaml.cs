using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Media;

namespace Amber.App.Views.Controls;

/// <summary>A file card (see the markup). The icon follows the file's kind, which can change when a received zip turns out to be a world.</summary>
public partial class FileCard : UserControl
{
    private TransferItem? _item;

    public FileCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = DataContext as TransferItem;
            if (_item is not null) _item.PropertyChanged += OnItemChanged;
            ShowIcon();
        };
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(TransferItem.IconKey)) ShowIcon();
    }

    private void ShowIcon() =>
        IconPart.Data = _item is not null && this.TryFindResource(_item.IconKey, out var icon) && icon is Geometry geometry ? geometry : null;
}
