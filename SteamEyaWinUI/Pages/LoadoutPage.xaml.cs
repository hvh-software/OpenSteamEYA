using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Models;
using SteamEyaWinUI.Services;
using Windows.Foundation;

namespace SteamEyaWinUI.Pages;

public sealed partial class LoadoutPage : Page, INotifyPropertyChanged
{
    private const double DragThresholdPixels = 8;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    private bool _currentCt;            // 当前编辑阵营：false=T，true=CT
    private bool _built;
    private CsLoadoutPreset _working = new();

    // 页面自实现的指针拖拽（不走系统 OLE 拖放：提权进程里 WinUI3 的 CanDrag/AllowDrop 整体失效，
    // 表现为「拖不动但左键点击有效」；指针事件不受提权影响，且对所有用户统一同一条路径）。
    // 状态收拢为单对象，操作结束即置 null，不存在跨拖拽的陈旧字段。
    private sealed class DragOperation
    {
        public UIElement Origin = null!;
        public uint PointerId;
        public uint Def;
        public CsLoadoutGroup Group;
        public uint? FromSlot;   // 非空=从已装备格子拖出（移动/交换）；空=从可选池拖出（装备）。
        public Point PressPosition;
        public bool Active;      // 超过拖拽阈值后才算真正开始
        public Border? Ghost;
        public TextBlock? GhostCaption;
    }

    private DragOperation? _drag;
    private CellView? _dropTarget;
    private bool _suppressNextTap;

    private readonly Dictionary<uint, CellView> _cells = new();
    private readonly ObservableCollection<LoadoutWeaponTile> _poolItems = new();

    public LoadoutPage()
    {
        InitializeComponent();
        PoolGrid.ItemsSource = _poolItems;
        TeamSelector.SelectedItem = TeamTItem;
        Loc.LanguageChanged += OnLanguageChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _working = AppState.SettingsService.Load().Loadout.Clone();
        BuildCells();
        RefreshAll();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 静态 x:Bind 文本随 Strings 重算；分组标题在代码里建，需重建；
            // 池 tile 的 DisplayName 是 OneTime 绑定，清空后让差量同步整体重建以换语言。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            if (_built)
            {
                BuildCells();
                _poolItems.Clear();
                RefreshAll();
            }
        });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_built)
            {
                BuildCells();
                RefreshAll();
            }
        });
    }

    // 一个固定格子的可视元素引用。
    private sealed class CellView
    {
        public Border Root = null!;
        public Border Bg = null!;
        public Image Image = null!;
        public TextBlock Empty = null!;
        public TextBlock Name = null!;
        public uint Slot;
        public CsLoadoutGroup Group;
    }

    private SolidColorBrush ThemeBrush(string key)
    {
        if (Resources.TryGetValue(key, out var local) && local is SolidColorBrush localBrush)
        {
            return localBrush;
        }

        if (Application.Current.Resources.TryGetValue(key, out var app) && app is SolidColorBrush appBrush)
        {
            return appBrush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 128, 160));
    }

    private void BuildCells()
    {
        _cells.Clear();
        BuildColumn(Col0Host,
        [
            (Loc.T("Loadout_Group_Starter"), CsLoadoutGroup.StarterPistol, [CsWeaponCatalog.StarterPistolSlot]),
            (Loc.T("Loadout_Group_Other"), CsLoadoutGroup.OtherPistol, CsWeaponCatalog.OtherPistolSlots)
        ]);
        BuildColumn(Col1Host, [(Loc.T("Loadout_Group_Mid"), CsLoadoutGroup.Mid, CsWeaponCatalog.MidSlots)], stretchCells: true);
        BuildColumn(Col2Host, [(Loc.T("Loadout_Group_Rifle"), CsLoadoutGroup.Rifle, CsWeaponCatalog.RifleSlots)], stretchCells: true);
        _built = true;
    }

    // 第一栏按内容决定整体高度；较短栏的格子均分剩余高度，与第一栏底部对齐。
    private void BuildColumn(
        Grid host,
        (string Title, CsLoadoutGroup Group, IReadOnlyList<uint> Slots)[] sections,
        bool stretchCells = false)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();

        var row = 0;
        foreach (var (title, group, slots) in sections)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock
            {
                Text = title,
                Style = (Style)Resources["LoadoutSectionHeaderStyle"],
                Margin = new Thickness(0, row == 0 ? 0 : 4, 0, 3)
            };
            Grid.SetRow(header, row);
            host.Children.Add(header);
            row++;

            foreach (var slot in slots)
            {
                host.RowDefinitions.Add(new RowDefinition
                {
                    Height = stretchCells ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
                });
                var cell = BuildCell(group, slot);
                Grid.SetRow(cell.Root, row);
                host.Children.Add(cell.Root);
                _cells[slot] = cell;
                row++;
            }
        }
    }

    private CellView BuildCell(CsLoadoutGroup group, uint slot)
    {
        var bg = new Border
        {
            Style = (Style)Resources["LoadoutCellBorderStyle"]
        };
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 80,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var empty = new TextBlock
        {
            Text = "+",
            Style = (Style)Resources["LoadoutEmptySlotStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 已装备武器名（左下角小字）：只有剪影认不出型号，尤其同类枪型。
        var name = new TextBlock
        {
            Style = (Style)Resources["LoadoutWeaponNameStyle"],
            FontSize = 13,
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Left,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        var content = new Grid();

        var mediaRow = new Grid
        {
            Margin = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        mediaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star) });
        mediaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });

        content.Children.Add(bg);
        Grid.SetColumn(image, 0);
        Grid.SetColumn(name, 1);
        mediaRow.Children.Add(image);
        mediaRow.Children.Add(name);
        content.Children.Add(mediaRow);

        content.Children.Add(empty);

        var root = new Border
        {
            Child = content,
            Margin = new Thickness(0, 0, 0, 12),
            MinHeight = 72
        };

        var cell = new CellView { Root = root, Bg = bg, Image = image, Empty = empty, Name = name, Slot = slot, Group = group };
        root.Tag = cell;
        root.PointerPressed += Cell_PointerPressed;
        root.PointerMoved += DragSource_PointerMoved;
        root.PointerReleased += DragSource_PointerReleased;
        root.PointerCaptureLost += DragSource_PointerCaptureLost;
        root.PointerCanceled += DragSource_PointerCanceled;
        root.Tapped += Cell_Tapped;
        return cell;
    }

    private void RefreshAll()
    {
        CancelDrag();
        var slots = _working.SlotsFor(_currentCt);
        foreach (var cell in _cells.Values)
        {
            UpdateCell(cell, slots);
        }

        SyncPool(slots);
    }

    private static void UpdateCell(CellView cell, Dictionary<uint, uint> slots)
    {
        if (slots.TryGetValue(cell.Slot, out var def) && CsWeaponCatalog.ByDef(def) is { } weapon)
        {
            cell.Image.Source = new SvgImageSource(new Uri(weapon.IconUri));
            cell.Image.Visibility = Visibility.Visible;
            cell.Empty.Visibility = Visibility.Collapsed;
            cell.Name.Text = weapon.LocalizedName;
            cell.Name.Visibility = Visibility.Visible;
        }
        else
        {
            cell.Image.Source = null;
            cell.Image.Visibility = Visibility.Collapsed;
            cell.Empty.Visibility = Visibility.Visible;
            cell.Name.Visibility = Visibility.Collapsed;
        }
    }

    // 差量同步可选池：装备/卸下只增删对应 tile，避免 Clear+全量重建让滚动位置跳回顶部、图标整片重载。
    private void SyncPool(Dictionary<uint, uint> slots)
    {
        var equipped = slots.Values.ToHashSet();
        var desired = new List<CsWeapon>();
        foreach (var group in CsWeaponCatalog.EditorGroups)
        {
            foreach (var weapon in CsWeaponCatalog.ForTeamGroup(_currentCt, group))
            {
                if (!equipped.Contains(weapon.Def))
                {
                    desired.Add(weapon);
                }
            }
        }

        var desiredDefs = desired.Select(w => w.Def).ToHashSet();
        for (var i = _poolItems.Count - 1; i >= 0; i--)
        {
            if (!desiredDefs.Contains(_poolItems[i].Weapon.Def))
            {
                _poolItems.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _poolItems.Count && _poolItems[i].Weapon.Def == desired[i].Def)
            {
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < _poolItems.Count; j++)
            {
                if (_poolItems[j].Weapon.Def == desired[i].Def)
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                _poolItems.Move(existing, i);
            }
            else
            {
                _poolItems.Insert(i, new LoadoutWeaponTile(desired[i]));
            }
        }
    }

    private void TeamSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _currentCt = TeamSelector.SelectedItem == TeamCtItem;
        if (_built)
        {
            RefreshAll();
        }
    }

    // ---- 手动指针拖拽 ----

    private void Cell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CellView cell ||
            !_working.SlotsFor(_currentCt).TryGetValue(cell.Slot, out var def) ||
            !IsPrimaryPress(e, cell.Root))
        {
            return;
        }

        BeginDrag(cell.Root, e, def, cell.Group, cell.Slot);
    }

    private void PoolItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement origin || !IsPrimaryPress(e, origin))
        {
            return;
        }

        var tile = origin.Tag as LoadoutWeaponTile ?? origin.DataContext as LoadoutWeaponTile;
        if (tile is null)
        {
            return;
        }

        BeginDrag(origin, e, tile.Weapon.Def, CsWeaponCatalog.GroupOf(tile.Weapon), fromSlot: null);
    }

    private static bool IsPrimaryPress(PointerRoutedEventArgs e, UIElement origin) =>
        e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
        e.GetCurrentPoint(origin).Properties.IsLeftButtonPressed;

    private void BeginDrag(UIElement origin, PointerRoutedEventArgs e, uint def, CsLoadoutGroup group, uint? fromSlot)
    {
        CancelDrag();
        _suppressNextTap = false;

        if (!origin.CapturePointer(e.Pointer))
        {
            return;
        }

        _drag = new DragOperation
        {
            Origin = origin,
            PointerId = e.Pointer.PointerId,
            Def = def,
            Group = group,
            FromSlot = fromSlot,
            PressPosition = e.GetCurrentPoint(DragLayer).Position
        };
    }

    private void DragSource_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag ||
            !ReferenceEquals(sender, drag.Origin) ||
            e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        // 统一用 DragLayer 坐标系：ghost 是 DragLayer(Canvas) 的子元素，命中检测也把格子换算到同一坐标系，
        // 避免 PageRoot 的 Padding 让 ghost 偏移、或与高亮的落格错位。
        var position = e.GetCurrentPoint(DragLayer).Position;
        if (!drag.Active)
        {
            var dx = position.X - drag.PressPosition.X;
            var dy = position.Y - drag.PressPosition.Y;
            if (dx * dx + dy * dy < DragThresholdPixels * DragThresholdPixels)
            {
                return;
            }

            ActivateDrag(drag);
        }

        Canvas.SetLeft(drag.Ghost!, position.X + 14);
        Canvas.SetTop(drag.Ghost!, position.Y + 10);
        UpdateDropTarget(position, drag);
    }

    private void DragSource_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag ||
            !ReferenceEquals(sender, drag.Origin) ||
            e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        var target = _dropTarget;
        var shouldDrop = drag.Active && target is not null;
        CancelDrag();

        if (shouldDrop)
        {
            PerformDrop(drag, target!);
        }
    }

    // 仅当丢失/取消的指针正是进行中拖拽的那一个时才取消，避免多指（触摸+笔）场景下抬起一个指针
    // 误杀另一个指针正在进行的拖拽。
    private void DragSource_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag && e.Pointer.PointerId == drag.PointerId)
        {
            CancelDrag();
        }
    }

    private void DragSource_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag && e.Pointer.PointerId == drag.PointerId)
        {
            CancelDrag();
        }
    }

    private void ActivateDrag(DragOperation drag)
    {
        drag.Active = true;
        // 拖拽真正启动后，原格子随后的 Tapped 不应再触发「点击卸下」。
        _suppressNextTap = true;

        var image = new Image { Width = 74, Height = 26, Stretch = Stretch.Uniform };
        if (CsWeaponCatalog.ByDef(drag.Def) is { } weapon)
        {
            image.Source = new SvgImageSource(new Uri(weapon.IconUri));
        }

        var caption = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(image);
        panel.Children.Add(caption);

        drag.GhostCaption = caption;
        drag.Ghost = new Border
        {
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7),
            IsHitTestVisible = false
        };
        drag.Ghost.Child = panel;
        DragLayer.Children.Add(drag.Ghost);
        UpdateGhostCaption(drag, validTarget: false);
    }

    private void UpdateDropTarget(Point position, DragOperation drag)
    {
        CellView? target = null;
        foreach (var cell in _cells.Values)
        {
            if (cell.Group != drag.Group)
            {
                continue;
            }

            var topLeft = cell.Root.TransformToVisual(DragLayer).TransformPoint(new Point(0, 0));
            if (position.X >= topLeft.X && position.X <= topLeft.X + cell.Root.ActualWidth &&
                position.Y >= topLeft.Y && position.Y <= topLeft.Y + cell.Root.ActualHeight)
            {
                target = cell;
                break;
            }
        }

        SetDropTarget(target);
        UpdateGhostCaption(drag, target is not null);
    }

    private void SetDropTarget(CellView? target)
    {
        if (ReferenceEquals(_dropTarget, target))
        {
            return;
        }

        if (_dropTarget is { } previous)
        {
            previous.Bg.ClearValue(Border.BorderBrushProperty);
            previous.Bg.ClearValue(Border.BorderThicknessProperty);
        }

        _dropTarget = target;
        if (target is not null)
        {
            target.Bg.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
            target.Bg.BorderThickness = new Thickness(2);
        }
    }

    private void UpdateGhostCaption(DragOperation drag, bool validTarget)
    {
        if (drag.GhostCaption is null || drag.Ghost is null)
        {
            return;
        }

        var name = CsWeaponCatalog.ByDef(drag.Def)?.LocalizedName ?? drag.Def.ToString();
        drag.GhostCaption.Text = validTarget
            ? $"{Loc.T(drag.FromSlot is null ? "Loadout_Drag_Equip" : "Loadout_Drag_Move")} · {name}"
            : name;
        drag.Ghost.Opacity = validTarget ? 1.0 : 0.7;
    }

    private void CancelDrag()
    {
        if (_drag is { } drag)
        {
            if (drag.Ghost is { } ghost)
            {
                DragLayer.Children.Remove(ghost);
            }

            // 必须显式释放 OS 级指针捕获：若拖拽经非 PointerReleased 路径结束（切阵营触发 RefreshAll、
            // 放置成功后 RefreshAll 等），未释放会让捕获泄漏到 origin，后续指针事件被错误路由、新拖拽无法激活。
            drag.Origin.ReleasePointerCaptures();
        }

        SetDropTarget(null);
        _drag = null;
    }

    private void PerformDrop(DragOperation drag, CellView cell)
    {
        if (cell.Group != drag.Group)
        {
            return;
        }

        var slots = _working.SlotsFor(_currentCt);

        if (drag.FromSlot is { } fromSlot)
        {
            // 已装备武器换位置：目标空则移动，目标有枪则交换。
            if (fromSlot == cell.Slot)
            {
                return;
            }

            if (slots.TryGetValue(cell.Slot, out var targetDef))
            {
                slots[cell.Slot] = drag.Def;
                slots[fromSlot] = targetDef;
            }
            else
            {
                slots[cell.Slot] = drag.Def;
                slots.Remove(fromSlot);
            }
        }
        else
        {
            // 从可选池装备（若原本有枪则被顶替，刷新后顶替下来的枪回到可选池）。
            slots[cell.Slot] = drag.Def;
        }

        Persist();
        RefreshAll();
    }

    private void Cell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

        if ((sender as FrameworkElement)?.Tag is CellView cell &&
            _working.SlotsFor(_currentCt).Remove(cell.Slot))
        {
            Persist();
            RefreshAll();
        }
    }

    private void Persist()
    {
        var settings = AppState.SettingsService.Load();
        settings.Loadout = _working.Clone();
        AppState.SettingsService.Save(settings);
    }
}
