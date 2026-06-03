using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.Views;

public partial class ScheduleTimeline : UserControl
{
    private const int SnapMinutes = 5;
    private const int DefaultNewBlockMinutes = 30;
    private const double EdgeHandleWidthPx = 6.0;
    private const int MinBlockMinutes = 5;

    private static readonly SolidColorBrush BlockFill   = new(Color.FromArgb(0xCC, 0xC0, 0x40, 0x40));
    private static readonly SolidColorBrush BlockStroke = new(Color.FromRgb(0xE0, 0x60, 0x60));
    private static readonly SolidColorBrush NowBrush    = new(Color.FromRgb(0xFF, 0xC8, 0x33));
    private static readonly SolidColorBrush OverrideOverlay =
        new(Color.FromArgb(0x44, 0xFF, 0xC8, 0x33));
    private static readonly SolidColorBrush HourTickBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LabelBrush    = new(Color.FromRgb(0xAA, 0xAA, 0xAA));

    private readonly DispatcherTimer _nowTimer;
    private Line? _nowLine;
    private Rectangle? _overrideOverlayRect;
    private bool _overrideActive;

    private enum DragMode { None, Move, ResizeStart, ResizeEnd }
    private DragMode _dragMode = DragMode.None;
    private int _dragIndex = -1;
    private double _dragMouseStartX;
    private int _dragOriginalStartMin;
    private int _dragOriginalEndMin;
    private Rectangle? _dragRect;

    public static readonly DependencyProperty EditedScheduleProperty =
        DependencyProperty.Register(
            nameof(EditedSchedule),
            typeof(ObservableCollection<ScheduleWindow>),
            typeof(ScheduleTimeline),
            new PropertyMetadata(null, OnEditedScheduleChanged));

    public ObservableCollection<ScheduleWindow>? EditedSchedule
    {
        get => (ObservableCollection<ScheduleWindow>?)GetValue(EditedScheduleProperty);
        set => SetValue(EditedScheduleProperty, value);
    }

    public static readonly DependencyProperty OverrideActiveProperty =
        DependencyProperty.Register(
            nameof(OverrideActive),
            typeof(bool),
            typeof(ScheduleTimeline),
            new PropertyMetadata(false, OnOverrideActiveChanged));

    public bool OverrideActive
    {
        get => (bool)GetValue(OverrideActiveProperty);
        set => SetValue(OverrideActiveProperty, value);
    }

    public ScheduleTimeline()
    {
        InitializeComponent();
        _nowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _nowTimer.Tick += (_, _) => RedrawNowMarker();
        Loaded += (_, _) => { _nowTimer.Start(); Rebuild(); };
        Unloaded += (_, _) => _nowTimer.Stop();
    }

    private static void OnEditedScheduleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ScheduleTimeline)d;
        if (e.OldValue is ObservableCollection<ScheduleWindow> oldColl)
            oldColl.CollectionChanged -= self.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<ScheduleWindow> newColl)
            newColl.CollectionChanged += self.OnCollectionChanged;
        self.Rebuild();
    }

    private static void OnOverrideActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ScheduleTimeline)d;
        self._overrideActive = (bool)e.NewValue;
        self.RedrawOverrideOverlay();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void HourAxis_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildHourLabels();

    private void Rebuild()
    {
        TimelineCanvas.Children.Clear();
        if (TimelineCanvas.ActualWidth <= 0 || TimelineCanvas.ActualHeight <= 0) return;

        // Hour grid lines
        var width = TimelineCanvas.ActualWidth;
        var height = TimelineCanvas.ActualHeight;
        for (var h = 0; h <= 24; h++)
        {
            var x = h / 24.0 * width;
            var line = new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = HourTickBrush,
                StrokeThickness = h % 6 == 0 ? 1.0 : 0.5,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            };
            TimelineCanvas.Children.Add(line);
        }

        // Block-window rectangles
        if (EditedSchedule != null)
        {
            for (var i = 0; i < EditedSchedule.Count; i++)
            {
                var w = EditedSchedule[i];
                var rect = MakeBlockRect(w, i, width, height);
                TimelineCanvas.Children.Add(rect);

                if (w.Days != "*")
                {
                    var label = new TextBlock
                    {
                        Text = w.Days,
                        Foreground = Brushes.White,
                        FontSize = 10,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(label, w.StartMin / 1440.0 * width + 4);
                    Canvas.SetTop(label, 4);
                    TimelineCanvas.Children.Add(label);
                }
            }
        }

        RedrawOverrideOverlay();
        RedrawNowMarker();
        RebuildHourLabels();
    }

    private Rectangle MakeBlockRect(ScheduleWindow w, int index, double width, double height)
    {
        var x = w.StartMin / 1440.0 * width;
        var rectWidth = Math.Max(1.0, (w.EndMin - w.StartMin) / 1440.0 * width);
        var rect = new Rectangle
        {
            Width = rectWidth,
            Height = height,
            Fill = BlockFill,
            Stroke = BlockStroke,
            StrokeThickness = 1.0,
            Cursor = Cursors.SizeAll,
            Tag = index,
            ToolTip = $"{ScheduleSerializer.FormatHhmm(w.StartMin)}-{ScheduleSerializer.FormatHhmm(w.EndMin)}"
                      + (w.Days == "*" ? string.Empty : $"  ({w.Days})"),
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, 0);
        rect.MouseLeftButtonDown += Rect_MouseLeftButtonDown;
        rect.MouseMove           += Rect_MouseMove;
        rect.MouseLeftButtonUp   += Rect_MouseLeftButtonUp;
        rect.LostMouseCapture    += Rect_LostMouseCapture;
        rect.MouseRightButtonDown += Rect_MouseRightButtonDown;
        return rect;
    }

    private void RedrawOverrideOverlay()
    {
        if (_overrideOverlayRect != null && TimelineCanvas.Children.Contains(_overrideOverlayRect))
            TimelineCanvas.Children.Remove(_overrideOverlayRect);
        _overrideOverlayRect = null;
        if (!_overrideActive || TimelineCanvas.ActualWidth <= 0) return;

        _overrideOverlayRect = new Rectangle
        {
            Width = TimelineCanvas.ActualWidth,
            Height = TimelineCanvas.ActualHeight,
            Fill = OverrideOverlay,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_overrideOverlayRect, 0);
        Canvas.SetTop(_overrideOverlayRect, 0);
        TimelineCanvas.Children.Add(_overrideOverlayRect);
    }

    private void RedrawNowMarker()
    {
        if (_nowLine != null && TimelineCanvas.Children.Contains(_nowLine))
            TimelineCanvas.Children.Remove(_nowLine);

        if (TimelineCanvas.ActualWidth <= 0) return;
        var now = DateTime.Now;
        var minutes = now.Hour * 60 + now.Minute;
        var x = minutes / 1440.0 * TimelineCanvas.ActualWidth;

        _nowLine = new Line
        {
            X1 = x, X2 = x, Y1 = 0, Y2 = TimelineCanvas.ActualHeight,
            Stroke = NowBrush,
            StrokeThickness = 2.0,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
        TimelineCanvas.Children.Add(_nowLine);
    }

    private void RebuildHourLabels()
    {
        HourAxis.Children.Clear();
        if (HourAxis.ActualWidth <= 0) return;
        var width = HourAxis.ActualWidth;
        for (var h = 0; h <= 24; h += 2)
        {
            var x = h / 24.0 * width;
            var label = new TextBlock
            {
                Text = h == 24 ? "24" : $"{h:D2}",
                Foreground = LabelBrush,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(0, x - label.DesiredSize.Width / 2));
            Canvas.SetTop(label, 3);
            HourAxis.Children.Add(label);
        }
    }

    // === Interaction ===

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != TimelineCanvas) return;
        if (EditedSchedule == null) return;

        var x = e.GetPosition(TimelineCanvas).X;
        var clickedMin = SnapToGrid(PixelToMinute(x));
        var startMin = Math.Clamp(clickedMin - DefaultNewBlockMinutes / 2,
                                  0, 1440 - DefaultNewBlockMinutes);
        var endMin = startMin + DefaultNewBlockMinutes;
        if (endMin - startMin < MinBlockMinutes) return;

        var line = $"* {ScheduleSerializer.FormatHhmm(startMin)}-{ScheduleSerializer.FormatHhmm(endMin)}";
        EditedSchedule.Add(new ScheduleWindow("*", startMin, endMin, line));
        e.Handled = true;
    }

    private void Rect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var rect = (Rectangle)sender;
        if (EditedSchedule == null) return;
        if (rect.Tag is not int idx || idx < 0 || idx >= EditedSchedule.Count) return;

        if (e.ClickCount == 2)
        {
            OpenPreciseEditDialog(idx);
            e.Handled = true;
            return;
        }

        var localX = e.GetPosition(rect).X;
        if (localX < EdgeHandleWidthPx)
        {
            _dragMode = DragMode.ResizeStart;
            rect.Cursor = Cursors.SizeWE;
        }
        else if (localX > rect.ActualWidth - EdgeHandleWidthPx)
        {
            _dragMode = DragMode.ResizeEnd;
            rect.Cursor = Cursors.SizeWE;
        }
        else
        {
            _dragMode = DragMode.Move;
            rect.Cursor = Cursors.SizeAll;
        }

        _dragIndex = idx;
        _dragRect = rect;
        _dragMouseStartX = e.GetPosition(TimelineCanvas).X;
        var w = EditedSchedule[idx];
        _dragOriginalStartMin = w.StartMin;
        _dragOriginalEndMin = w.EndMin;
        rect.CaptureMouse();
        e.Handled = true;
    }

    private void Rect_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || _dragRect == null) return;
        if (EditedSchedule == null) return;
        if (_dragIndex < 0 || _dragIndex >= EditedSchedule.Count) return;

        var dxPx = e.GetPosition(TimelineCanvas).X - _dragMouseStartX;
        var dxMin = SnapToGrid((int)Math.Round(dxPx / TimelineCanvas.ActualWidth * 1440.0));

        int newStart = _dragOriginalStartMin;
        int newEnd   = _dragOriginalEndMin;

        switch (_dragMode)
        {
            case DragMode.Move:
                newStart = _dragOriginalStartMin + dxMin;
                newEnd   = _dragOriginalEndMin   + dxMin;
                if (newStart < 0) { newEnd -= newStart; newStart = 0; }
                if (newEnd > 1440) { newStart -= (newEnd - 1440); newEnd = 1440; }
                break;
            case DragMode.ResizeStart:
                newStart = _dragOriginalStartMin + dxMin;
                newStart = Math.Clamp(newStart, 0, _dragOriginalEndMin - MinBlockMinutes);
                break;
            case DragMode.ResizeEnd:
                newEnd = _dragOriginalEndMin + dxMin;
                newEnd = Math.Clamp(newEnd, _dragOriginalStartMin + MinBlockMinutes, 1440);
                break;
        }

        var current = EditedSchedule[_dragIndex];
        if (current.StartMin == newStart && current.EndMin == newEnd) return;

        var raw = current.Days == "*"
            ? $"{ScheduleSerializer.FormatHhmm(newStart)}-{ScheduleSerializer.FormatHhmm(newEnd)}"
            : $"{current.Days} {ScheduleSerializer.FormatHhmm(newStart)}-{ScheduleSerializer.FormatHhmm(newEnd)}";
        EditedSchedule[_dragIndex] = current with { StartMin = newStart, EndMin = newEnd, Raw = raw };
    }

    private void Rect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag((Rectangle)sender);
    private void Rect_LostMouseCapture(object sender, MouseEventArgs e)      => EndDrag((Rectangle)sender);

    private void EndDrag(Rectangle rect)
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragRect = null;
        rect.Cursor = Cursors.SizeAll;
        if (rect.IsMouseCaptured) rect.ReleaseMouseCapture();
    }

    private void Rect_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var rect = (Rectangle)sender;
        if (EditedSchedule == null) return;
        if (rect.Tag is not int idx || idx < 0 || idx >= EditedSchedule.Count) return;
        EditedSchedule.RemoveAt(idx);
        e.Handled = true;
    }

    private void OpenPreciseEditDialog(int idx)
    {
        if (EditedSchedule == null) return;
        if (idx < 0 || idx >= EditedSchedule.Count) return;
        var w = EditedSchedule[idx];

        var dlg = new ScheduleEditDialog(w) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result is { } edited)
        {
            EditedSchedule[idx] = edited;
        }
    }

    private int PixelToMinute(double x) =>
        TimelineCanvas.ActualWidth <= 0
            ? 0
            : Math.Clamp((int)Math.Round(x / TimelineCanvas.ActualWidth * 1440.0), 0, 1440);

    private static int SnapToGrid(int minutes) => (int)Math.Round(minutes / (double)SnapMinutes) * SnapMinutes;
}
