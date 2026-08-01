using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MinecraftServerManager.Controls;

public sealed partial class ResourceSparkline : UserControl
{
    private const int SampleCapacity = 60;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(850);

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(object),
        typeof(ResourceSparkline),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(ResourceSparkline),
        new PropertyMetadata(double.NaN, OnScaleChanged));

    public static readonly DependencyProperty MinimumRangeProperty = DependencyProperty.Register(
        nameof(MinimumRange),
        typeof(double),
        typeof(ResourceSparkline),
        new PropertyMetadata(1d, OnScaleChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(ResourceSparkline),
        new PropertyMetadata(null, OnLineBrushChanged));

    private readonly DispatcherQueueTimer _animationTimer;
    private INotifyCollectionChanged? _observedCollection;
    private IReadOnlyList<double> _displayValues = Array.Empty<double>();
    private IReadOnlyList<double> _animationStart = Array.Empty<double>();
    private IReadOnlyList<double> _animationTarget = Array.Empty<double>();
    private DateTimeOffset _animationStartedAt;
    private double _animationProgress;
    private double _displayScaleMinimum;
    private double _displayScaleMaximum = 1d;
    private double _startScaleMinimum;
    private double _startScaleMaximum = 1d;
    private double _targetScaleMinimum;
    private double _targetScaleMaximum = 1d;
    private bool _refreshQueued;
    private bool _isAppendAnimation;

    public ResourceSparkline()
    {
        InitializeComponent();
        _animationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _animationTimer.Tick += AnimationTimer_Tick;
        Unloaded += ResourceSparkline_Unloaded;
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double MinimumRange
    {
        get => (double)GetValue(MinimumRangeProperty);
        set => SetValue(MinimumRangeProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var graph = (ResourceSparkline)dependencyObject;
        graph.DetachCollection();
        graph.AttachCollection(args.NewValue);
        graph.SetCurrentValuesImmediately();
    }

    private static void OnScaleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var graph = (ResourceSparkline)dependencyObject;
        (graph._displayScaleMinimum, graph._displayScaleMaximum) = graph.GetScale(graph._displayValues);
        graph.RenderCurrentFrame();
    }

    private static void OnLineBrushChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var graph = (ResourceSparkline)dependencyObject;
        graph.HistoryLine.Stroke = args.NewValue as Brush;
        graph.HistoryArea.Fill = args.NewValue as Brush;
    }

    private void AttachCollection(object? value)
    {
        _observedCollection = value as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged += ObservedCollection_CollectionChanged;
        }
    }

    private void DetachCollection()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= ObservedCollection_CollectionChanged;
            _observedCollection = null;
        }
    }

    private void ObservedCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            BeginAnimationToCurrentValues();
        }))
        {
            _refreshQueued = false;
            SetCurrentValuesImmediately();
        }
    }

    private void SetCurrentValuesImmediately()
    {
        _animationTimer.Stop();
        _isAppendAnimation = false;
        _animationProgress = 1d;
        _displayValues = ReadValues();
        (_displayScaleMinimum, _displayScaleMaximum) = GetScale(_displayValues);
        RenderValues(_displayValues, _displayScaleMinimum, _displayScaleMaximum);
    }

    private void BeginAnimationToCurrentValues()
    {
        var target = ReadValues();
        if (_displayValues.Count == 0 || target.Count == 0 || !IsSingleAppend(_displayValues, target))
        {
            _displayValues = target;
            _animationTimer.Stop();
            _isAppendAnimation = false;
            (_displayScaleMinimum, _displayScaleMaximum) = GetScale(target);
            RenderValues(target, _displayScaleMinimum, _displayScaleMaximum);
            return;
        }

        _animationStart = _displayValues;
        _animationTarget = target;
        _startScaleMinimum = _displayScaleMinimum;
        _startScaleMaximum = _displayScaleMaximum;
        (_targetScaleMinimum, _targetScaleMaximum) = GetScale(target);
        _animationProgress = 0d;
        _animationStartedAt = DateTimeOffset.UtcNow;
        _isAppendAnimation = true;
        _animationTimer.Start();
        RenderCurrentFrame();
    }

    private void AnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = DateTimeOffset.UtcNow - _animationStartedAt;
        _animationProgress = Math.Clamp(
            elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds,
            0d,
            1d);
        RenderCurrentFrame();

        if (_animationProgress < 1d)
        {
            return;
        }

        sender.Stop();
        _isAppendAnimation = false;
        _displayValues = _animationTarget;
        _displayScaleMinimum = _targetScaleMinimum;
        _displayScaleMaximum = _targetScaleMaximum;
        RenderValues(_displayValues, _displayScaleMinimum, _displayScaleMaximum);
    }

    private void RenderCurrentFrame()
    {
        if (!_isAppendAnimation)
        {
            RenderValues(_displayValues, _displayScaleMinimum, _displayScaleMaximum);
            return;
        }

        var scaleMinimum = Lerp(_startScaleMinimum, _targetScaleMinimum, _animationProgress);
        var scaleMaximum = Lerp(_startScaleMaximum, _targetScaleMaximum, _animationProgress);
        RenderAppendFrame(scaleMinimum, scaleMaximum, _animationProgress);
    }

    private IReadOnlyList<double> ReadValues()
    {
        if (ItemsSource is not IEnumerable source)
        {
            return Array.Empty<double>();
        }

        return source
            .Cast<object?>()
            .Select(value => value is null ? 0d : Convert.ToDouble(value))
            .Where(double.IsFinite)
            .TakeLast(SampleCapacity)
            .ToArray();
    }

    private static bool IsSingleAppend(IReadOnlyList<double> previous, IReadOnlyList<double> current)
    {
        if (previous.Count < SampleCapacity && current.Count == previous.Count + 1)
        {
            return previous.SequenceEqual(current.Take(previous.Count));
        }

        return previous.Count == SampleCapacity
            && current.Count == SampleCapacity
            && previous.Skip(1).SequenceEqual(current.Take(SampleCapacity - 1));
    }

    private void RenderValues(IReadOnlyList<double> values, double minimum, double maximum)
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        UpdateGuides(width, height);
        if (values.Count == 0)
        {
            HistoryLine.Data = null;
            HistoryArea.Data = null;
            return;
        }

        var slotWidth = width / (SampleCapacity - 1d);
        var startSlot = SampleCapacity - values.Count;
        var points = new Point[values.Count];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = CreatePoint(
                (startSlot + index) * slotWidth,
                values[index],
                minimum,
                maximum,
                height);
        }

        SetGeometry(points, height);
    }

    private void RenderAppendFrame(double minimum, double maximum, double progress)
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || _animationStart.Count == 0)
        {
            return;
        }

        UpdateGuides(width, height);
        var slotWidth = width / (SampleCapacity - 1d);
        var startSlot = SampleCapacity - _animationStart.Count;
        var points = new Point[_animationStart.Count + 1];

        for (var index = 0; index < _animationStart.Count; index++)
        {
            points[index] = CreatePoint(
                (startSlot + index - progress) * slotWidth,
                _animationStart[index],
                minimum,
                maximum,
                height);
        }

        var incomingValue = Lerp(_animationStart[^1], _animationTarget[^1], progress);
        points[^1] = CreatePoint(width, incomingValue, minimum, maximum, height);
        SetGeometry(points, height);
    }

    private static Point CreatePoint(
        double x,
        double value,
        double minimum,
        double maximum,
        double height)
    {
        var usableHeight = Math.Max(1d, height - 4d);
        var normalized = Math.Clamp((value - minimum) / Math.Max(0.0001d, maximum - minimum), 0d, 1d);
        return new Point(x, 2d + ((1d - normalized) * usableHeight));
    }

    private void SetGeometry(IReadOnlyList<Point> points, double height)
    {
        HistoryLine.Data = CreateSmoothGeometry(points, closeToBottom: false, height);
        HistoryArea.Data = CreateSmoothGeometry(points, closeToBottom: true, height);
    }

    private (double Minimum, double Maximum) GetScale(IReadOnlyList<double> values)
    {
        if (double.IsFinite(Maximum) && Maximum > 0)
        {
            return (0d, Maximum);
        }

        if (values.Count == 0)
        {
            return (0d, Math.Max(1d, MinimumRange));
        }

        var minimum = values.Min();
        var maximum = values.Max();
        var range = Math.Max(maximum - minimum, Math.Max(1d, MinimumRange));
        var padding = range * 0.12d;
        return (Math.Max(0d, minimum - padding), maximum + padding);
    }

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * progress);

    private static PathGeometry CreateSmoothGeometry(IReadOnlyList<Point> points, bool closeToBottom, double height)
    {
        var figure = new PathFigure
        {
            StartPoint = closeToBottom ? new Point(points[0].X, height) : points[0],
            IsClosed = closeToBottom,
        };

        if (closeToBottom)
        {
            figure.Segments.Add(new LineSegment { Point = points[0] });
        }

        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            var middleX = (previous.X + current.X) / 2d;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(middleX, previous.Y),
                Point2 = new Point(middleX, current.Y),
                Point3 = current,
            });
        }

        if (closeToBottom)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, height) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void UpdateGuides(double width, double height)
    {
        foreach (var (line, y) in new[] { (UpperGuide, height * 0.33d), (LowerGuide, height * 0.67d) })
        {
            line.X1 = 0;
            line.X2 = width;
            line.Y1 = y;
            line.Y2 = y;
        }
    }

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ChartCanvas.Width = args.NewSize.Width;
        ChartCanvas.Height = Math.Max(0d, args.NewSize.Height - 10d);
        ChartCanvas.Clip = new RectangleGeometry
        {
            Rect = new Rect(0d, 0d, ChartCanvas.Width, ChartCanvas.Height)
        };
        RenderCurrentFrame();
    }

    private void ResourceSparkline_Unloaded(object sender, RoutedEventArgs args)
    {
        _animationTimer.Stop();
        DetachCollection();
    }
}
