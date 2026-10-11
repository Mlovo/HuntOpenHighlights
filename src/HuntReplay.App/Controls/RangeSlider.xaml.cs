using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HuntReplay.App.Controls;

public enum RangeSliderHandle
{
    None,
    Start,
    End,
    Range,
    Playhead
}

public sealed class RangeSeekEventArgs : RoutedEventArgs
{
    public double Timestamp { get; }
    public RangeSliderHandle Handle { get; }

    public RangeSeekEventArgs(RoutedEvent routedEvent, double timestamp, RangeSliderHandle handle)
        : base(routedEvent)
    {
        Timestamp = timestamp;
        Handle = handle;
    }
}

public partial class RangeSlider : UserControl
{
    #region Routed Events

    public static readonly RoutedEvent LiveSeekEvent =
        EventManager.RegisterRoutedEvent(nameof(LiveSeek), RoutingStrategy.Bubble, typeof(EventHandler<RangeSeekEventArgs>), typeof(RangeSlider));

    public event EventHandler<RangeSeekEventArgs> LiveSeek
    {
        add => AddHandler(LiveSeekEvent, value);
        remove => RemoveHandler(LiveSeekEvent, value);
    }

    public static readonly RoutedEvent SelectionStartChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(SelectionStartChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<double>), typeof(RangeSlider));

    public event RoutedPropertyChangedEventHandler<double> SelectionStartChanged
    {
        add => AddHandler(SelectionStartChangedEvent, value);
        remove => RemoveHandler(SelectionStartChangedEvent, value);
    }

    public static readonly RoutedEvent SelectionEndChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(SelectionEndChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<double>), typeof(RangeSlider));

    public event RoutedPropertyChangedEventHandler<double> SelectionEndChanged
    {
        add => AddHandler(SelectionEndChangedEvent, value);
        remove => RemoveHandler(SelectionEndChangedEvent, value);
    }

    public static readonly RoutedEvent PlayheadChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(PlayheadChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<double>), typeof(RangeSlider));

    public event RoutedPropertyChangedEventHandler<double> PlayheadChanged
    {
        add => AddHandler(PlayheadChangedEvent, value);
        remove => RemoveHandler(PlayheadChangedEvent, value);
    }

    public static readonly RoutedEvent RangeDragStartedEvent =
        EventManager.RegisterRoutedEvent(nameof(RangeDragStarted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RangeSlider));

    public event RoutedEventHandler RangeDragStarted
    {
        add => AddHandler(RangeDragStartedEvent, value);
        remove => RemoveHandler(RangeDragStartedEvent, value);
    }

    public static readonly RoutedEvent RangeDragCompletedEvent =
        EventManager.RegisterRoutedEvent(nameof(RangeDragCompleted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RangeSlider));

    public event RoutedEventHandler RangeDragCompleted
    {
        add => AddHandler(RangeDragCompletedEvent, value);
        remove => RemoveHandler(RangeDragCompletedEvent, value);
    }

    #endregion

    #region Dependency Properties

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnMinimumChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender, OnMaximumChanged));

    public static readonly DependencyProperty RangeStartProperty =
        DependencyProperty.Register(nameof(RangeStart), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnRangeStartChanged, CoerceRangeStart));

    public static readonly DependencyProperty RangeEndProperty =
        DependencyProperty.Register(nameof(RangeEnd), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnRangeEndChanged, CoerceRangeEnd));

    public static readonly DependencyProperty SelectionStartProperty =
        DependencyProperty.Register(nameof(SelectionStart), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectionStartChanged, CoerceRangeStart));

    public static readonly DependencyProperty SelectionEndProperty =
        DependencyProperty.Register(nameof(SelectionEnd), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectionEndChanged, CoerceRangeEnd));

    public static readonly DependencyProperty PlayheadProperty =
        DependencyProperty.Register(nameof(Playhead), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnPlayheadChanged, CoercePlayhead));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged, CoercePlayhead));

    public static readonly DependencyProperty MinRangeProperty =
        DependencyProperty.Register(nameof(MinRange), typeof(double), typeof(RangeSlider),
            new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender, OnMinRangeChanged));

    public static readonly DependencyProperty IsPlayheadVisibleProperty =
        DependencyProperty.Register(nameof(IsPlayheadVisible), typeof(bool), typeof(RangeSlider),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnIsPlayheadVisibleChanged));

    public static readonly DependencyProperty IsDraggingProperty =
        DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(RangeSlider),
            new FrameworkPropertyMetadata(false));

    #endregion

    #region CLR Properties

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double RangeStart
    {
        get => (double)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public double RangeEnd
    {
        get => (double)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public double SelectionStart
    {
        get => (double)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public double SelectionEnd
    {
        get => (double)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public double Playhead
    {
        get => (double)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MinRange
    {
        get => (double)GetValue(MinRangeProperty);
        set => SetValue(MinRangeProperty, value);
    }

    public bool IsPlayheadVisible
    {
        get => (bool)GetValue(IsPlayheadVisibleProperty);
        set => SetValue(IsPlayheadVisibleProperty, value);
    }

    public bool IsDragging
    {
        get => (bool)GetValue(IsDraggingProperty);
        private set => SetValue(IsDraggingProperty, value);
    }

    public double EffectiveMinRange
    {
        get
        {
            double min = Math.Min(Minimum, Maximum);
            double max = Math.Max(Minimum, Maximum);
            return Math.Max(0.0, Math.Min(Math.Max(0.0, MinRange), max - min));
        }
    }

    #endregion

    private Point rangeBarStartPoint;
    private double rangeBarStartRangeStart;
    private double rangeBarStartRangeEnd;
    private bool isDraggingRangeBar;

    public RangeSlider()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayoutPositions();
    }

    #region Layout & Usable Width

    public double GetUsableWidth()
    {
        double actualWidth = TrackCanvas?.ActualWidth ?? ActualWidth;
        if (actualWidth <= 0.0) actualWidth = ActualWidth;
        return Math.Max(1.0, actualWidth - 14.0);
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutPositions();
    }

    public void SetRange(double start, double end)
    {
        if (start > RangeStart || end > RangeEnd)
        {
            RangeEnd = end;
            RangeStart = start;
        }
        else
        {
            RangeStart = start;
            RangeEnd = end;
        }
    }

    public void UpdateLayoutPositions()
    {
        if (TrackCanvas is null || StartThumb is null || EndThumb is null ||
            SelectedRangeBar is null || UnselectedLeftTrack is null ||
            UnselectedRightTrack is null || PlayheadThumb is null) return;

        double actualWidth = TrackCanvas.ActualWidth;
        if (actualWidth <= 0.0) actualWidth = ActualWidth;
        if (actualWidth <= 0.0) return;

        const double thumbWidth = 14.0;
        const double playheadWidth = 10.0;
        double usableWidth = Math.Max(1.0, actualWidth - thumbWidth);
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double range = Math.Max(0.001, max - min);

        double startRatio = Math.Clamp((RangeStart - min) / range, 0.0, 1.0);
        double endRatio = Math.Clamp((RangeEnd - min) / range, 0.0, 1.0);
        double playheadRatio = Math.Clamp((Playhead - min) / range, 0.0, 1.0);

        double startX = startRatio * usableWidth;
        double endX = endRatio * usableWidth;

        Canvas.SetLeft(StartThumb, startX);
        Canvas.SetLeft(EndThumb, endX);

        double barX = startX + (thumbWidth / 2.0);
        double barWidth = Math.Max(0.0, endX - startX);
        Canvas.SetLeft(SelectedRangeBar, barX);
        SelectedRangeBar.Width = barWidth;

        Canvas.SetLeft(UnselectedLeftTrack, 0.0);
        UnselectedLeftTrack.Width = Math.Max(0.0, barX);

        double rightX = barX + barWidth;
        Canvas.SetLeft(UnselectedRightTrack, rightX);
        UnselectedRightTrack.Width = Math.Max(0.0, actualWidth - rightX);

        double playheadCenter = (thumbWidth / 2.0) + (playheadRatio * usableWidth);
        double playheadLeft = Math.Clamp(playheadCenter - (playheadWidth / 2.0), 0.0, Math.Max(0.0, actualWidth - playheadWidth));
        Canvas.SetLeft(PlayheadThumb, playheadLeft);

        // Hide playhead if explicitly invisible, or when coinciding with the boundary handles so it does not obscure them
        bool coincidesWithHandles = Math.Abs(playheadCenter - (startX + thumbWidth / 2.0)) < 6.0 ||
                                    Math.Abs(playheadCenter - (endX + thumbWidth / 2.0)) < 6.0;

        PlayheadThumb.Visibility = (IsPlayheadVisible && !coincidesWithHandles) ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Coercion & Property Changed Handlers

    private static object CoerceRangeStart(DependencyObject d, object baseValue)
    {
        var slider = (RangeSlider)d;
        double val = (double)baseValue;
        double min = Math.Min(slider.Minimum, slider.Maximum);
        double max = Math.Max(slider.Minimum, slider.Maximum);
        if (double.IsNaN(val) || double.IsInfinity(val)) return min;
        double maxStart = Math.Min(max - slider.EffectiveMinRange, slider.RangeEnd - slider.EffectiveMinRange);
        maxStart = Math.Clamp(maxStart, min, max);
        return Math.Clamp(val, min, maxStart);
    }

    private static object CoerceRangeEnd(DependencyObject d, object baseValue)
    {
        var slider = (RangeSlider)d;
        double val = (double)baseValue;
        double min = Math.Min(slider.Minimum, slider.Maximum);
        double max = Math.Max(slider.Minimum, slider.Maximum);
        if (double.IsNaN(val) || double.IsInfinity(val)) return max;
        double minEnd = Math.Max(min + slider.EffectiveMinRange, slider.RangeStart + slider.EffectiveMinRange);
        minEnd = Math.Clamp(minEnd, min, max);
        return Math.Clamp(val, minEnd, max);
    }

    private static object CoercePlayhead(DependencyObject d, object baseValue)
    {
        var slider = (RangeSlider)d;
        double val = (double)baseValue;
        double min = Math.Min(slider.Minimum, slider.Maximum);
        double max = Math.Max(slider.Minimum, slider.Maximum);
        if (double.IsNaN(val) || double.IsInfinity(val)) return min;
        return Math.Clamp(val, min, max);
    }

    private static void OnMinimumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.CoerceValue(RangeStartProperty);
        slider.CoerceValue(RangeEndProperty);
        slider.CoerceValue(RangeStartProperty);
        slider.CoerceValue(SelectionStartProperty);
        slider.CoerceValue(SelectionEndProperty);
        slider.CoerceValue(PlayheadProperty);
        slider.CoerceValue(ValueProperty);
        slider.UpdateLayoutPositions();
    }

    private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.CoerceValue(RangeEndProperty);
        slider.CoerceValue(RangeStartProperty);
        slider.CoerceValue(RangeEndProperty);
        slider.CoerceValue(SelectionEndProperty);
        slider.CoerceValue(SelectionStartProperty);
        slider.CoerceValue(PlayheadProperty);
        slider.CoerceValue(ValueProperty);
        slider.UpdateLayoutPositions();
    }

    private static void OnMinRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.CoerceValue(RangeEndProperty);
        slider.CoerceValue(RangeStartProperty);
        slider.CoerceValue(SelectionEndProperty);
        slider.CoerceValue(SelectionStartProperty);
        slider.UpdateLayoutPositions();
    }

    private static void OnRangeStartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double oldVal = (double)e.OldValue;
        double newVal = (double)e.NewValue;

        if (Math.Abs(slider.SelectionStart - newVal) > 1e-6)
        {
            slider.SelectionStart = newVal;
        }

        slider.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(oldVal, newVal, SelectionStartChangedEvent));
        slider.UpdateLayoutPositions();
    }

    private static void OnSelectionStartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double newVal = (double)e.NewValue;
        if (Math.Abs(slider.RangeStart - newVal) > 1e-6)
        {
            slider.RangeStart = newVal;
        }
    }

    private static void OnRangeEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double oldVal = (double)e.OldValue;
        double newVal = (double)e.NewValue;

        if (Math.Abs(slider.SelectionEnd - newVal) > 1e-6)
        {
            slider.SelectionEnd = newVal;
        }

        slider.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(oldVal, newVal, SelectionEndChangedEvent));
        slider.UpdateLayoutPositions();
    }

    private static void OnSelectionEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double newVal = (double)e.NewValue;
        if (Math.Abs(slider.RangeEnd - newVal) > 1e-6)
        {
            slider.RangeEnd = newVal;
        }
    }

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double oldVal = (double)e.OldValue;
        double newVal = (double)e.NewValue;

        if (Math.Abs(slider.Value - newVal) > 1e-6)
        {
            slider.Value = newVal;
        }

        slider.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(oldVal, newVal, PlayheadChangedEvent));
        slider.UpdateLayoutPositions();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        double newVal = (double)e.NewValue;
        if (Math.Abs(slider.Playhead - newVal) > 1e-6)
        {
            slider.Playhead = newVal;
        }
    }

    private static void OnIsPlayheadVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;
        slider.UpdateLayoutPositions();
    }

    #endregion

    #region Mouse & Drag Interaction

    private void OnStartThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        IsDragging = true;
        RaiseEvent(new RoutedEventArgs(RangeDragStartedEvent));
    }

    private void OnStartThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        double usableWidth = GetUsableWidth();
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double range = Math.Max(0.001, max - min);
        double deltaSeconds = (e.HorizontalChange / usableWidth) * range;
        double effectiveMin = EffectiveMinRange;
        double maxStart = Math.Clamp(RangeEnd - effectiveMin, min, max);
        double newStart = Math.Clamp(RangeStart + deltaSeconds, min, maxStart);

        RangeStart = newStart;
        RaiseEvent(new RangeSeekEventArgs(LiveSeekEvent, newStart, RangeSliderHandle.Start));
    }

    private void OnStartThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        IsDragging = false;
        RaiseEvent(new RoutedEventArgs(RangeDragCompletedEvent));
    }

    private void OnEndThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        IsDragging = true;
        RaiseEvent(new RoutedEventArgs(RangeDragStartedEvent));
    }

    private void OnEndThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        double usableWidth = GetUsableWidth();
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double range = Math.Max(0.001, max - min);
        double deltaSeconds = (e.HorizontalChange / usableWidth) * range;
        double effectiveMin = EffectiveMinRange;
        double minEnd = Math.Clamp(RangeStart + effectiveMin, min, max);
        double newEnd = Math.Clamp(RangeEnd + deltaSeconds, minEnd, max);

        RangeEnd = newEnd;
        RaiseEvent(new RangeSeekEventArgs(LiveSeekEvent, newEnd, RangeSliderHandle.End));
    }

    private void OnEndThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        IsDragging = false;
        RaiseEvent(new RoutedEventArgs(RangeDragCompletedEvent));
    }

    private void OnPlayheadDragStarted(object sender, DragStartedEventArgs e)
    {
        IsDragging = true;
        RaiseEvent(new RoutedEventArgs(RangeDragStartedEvent));
    }

    private void OnPlayheadDragDelta(object sender, DragDeltaEventArgs e)
    {
        double usableWidth = GetUsableWidth();
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double range = Math.Max(0.001, max - min);
        double deltaSeconds = (e.HorizontalChange / usableWidth) * range;
        double newPlayhead = Math.Clamp(Playhead + deltaSeconds, min, max);

        Playhead = newPlayhead;
        RaiseEvent(new RangeSeekEventArgs(LiveSeekEvent, newPlayhead, RangeSliderHandle.Playhead));
    }

    private void OnPlayheadDragCompleted(object sender, DragCompletedEventArgs e)
    {
        IsDragging = false;
        RaiseEvent(new RoutedEventArgs(RangeDragCompletedEvent));
    }

    private void OnRangeBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            isDraggingRangeBar = true;
            IsDragging = true;
            rangeBarStartPoint = e.GetPosition(RootGrid);
            rangeBarStartRangeStart = RangeStart;
            rangeBarStartRangeEnd = RangeEnd;
            SelectedRangeBar.CaptureMouse();
            RaiseEvent(new RoutedEventArgs(RangeDragStartedEvent));
            e.Handled = true;
        }
    }

    private void OnRangeBarMouseMove(object sender, MouseEventArgs e)
    {
        if (isDraggingRangeBar && e.LeftButton == MouseButtonState.Pressed)
        {
            Point current = e.GetPosition(RootGrid);
            double deltaX = current.X - rangeBarStartPoint.X;
            double usableWidth = GetUsableWidth();
            double min = Math.Min(Minimum, Maximum);
            double max = Math.Max(Minimum, Maximum);
            double range = Math.Max(0.001, max - min);
            double deltaSec = (deltaX / usableWidth) * range;
            double duration = Math.Max(0.0, rangeBarStartRangeEnd - rangeBarStartRangeStart);

            double candStart = rangeBarStartRangeStart + deltaSec;
            double candEnd = rangeBarStartRangeEnd + deltaSec;

            if (candEnd > max)
            {
                candEnd = max;
                candStart = Math.Max(min, max - duration);
            }
            if (candStart < min)
            {
                candStart = min;
                candEnd = Math.Min(max, min + duration);
            }

            if (deltaSec >= 0)
            {
                RangeEnd = candEnd;
                RangeStart = candStart;
            }
            else
            {
                RangeStart = candStart;
                RangeEnd = candEnd;
            }

            RaiseEvent(new RangeSeekEventArgs(LiveSeekEvent, candStart, RangeSliderHandle.Range));
            e.Handled = true;
        }
    }

    private void OnRangeBarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (isDraggingRangeBar)
        {
            isDraggingRangeBar = false;
            IsDragging = false;
            SelectedRangeBar.ReleaseMouseCapture();
            RaiseEvent(new RoutedEventArgs(RangeDragCompletedEvent));
            e.Handled = true;
        }
    }

    private void OnTrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == StartThumb || e.OriginalSource == EndThumb ||
            e.OriginalSource == PlayheadThumb || e.OriginalSource == SelectedRangeBar ||
            isDraggingRangeBar) return;

        Point pos = e.GetPosition(TrackCanvas);
        double usableWidth = GetUsableWidth();
        double min = Math.Min(Minimum, Maximum);
        double max = Math.Max(Minimum, Maximum);
        double ratio = Math.Clamp((pos.X - 7.0) / usableWidth, 0.0, 1.0);
        double target = min + ratio * (max - min);
        Playhead = target;
        RaiseEvent(new RangeSeekEventArgs(LiveSeekEvent, target, RangeSliderHandle.Playhead));
    }

    #endregion
}
