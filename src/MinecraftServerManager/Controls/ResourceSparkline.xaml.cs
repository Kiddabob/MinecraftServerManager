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
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(320);

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
    private IReadOnlyList<double> _animationStart = Array.Empty<double>();
    private IReadOnlyList<double> _animationTarget = Array.Empty<double>();
    private IReadOnlyList<double> _displayValues = Array.Empty<double>();
    private DateTimeOffset _animationStartedAt;

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
        graph.BeginAnimationToCurrentValues();
    }

    private static void OnScaleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((ResourceSparkline)dependencyObject).RenderValues();
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

    private void ObservedCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        BeginAnimationToCurrentValues();
    }

    private void BeginAnimationToCurrentValues()
    {
        var target = ReadValues();
        if (_displayValues.Count == 0 || target.Count == 0)
        {
            _displayValues = target;
            _animationTimer.Stop();
            RenderValues();
            return;
        }

        _animationStart = AlignValues(_displayValues, target.Count);
        _animationTarget = target;
        _animationStartedAt = DateTimeOffset.UtcNow;
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = DateTimeOffset.UtcNow - _animationStartedAt;
        var progress = Math.Clamp(elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds, 0d, 1d);
        var easedProgress = 1d - Math.Pow(1d - progress, 3d);
        var values = new double[_animationTarget.Count];

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = _animationStart[index]
                + ((_animationTarget[index] - _animationStart[index]) * easedProgress);
        }

        _displayValues = values;
        RenderValues();

        if (progress >= 1d)
        {
            sender.Stop();
            _displayValues = _animationTarget;
        }
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
            .ToArray();
    }

    private static IReadOnlyList<double> AlignValues(IReadOnlyList<double> values, int targetCount)
    {
        if (values.Count == targetCount)
        {
            return values;
        }

        if (values.Count > targetCount)
        {
            return values.Skip(values.Count - targetCount).ToArray();
        }

        var result = new double[targetCount];
        var padValue = values.Count == 0 ? 0d : values[0];
        var padding = targetCount - values.Count;
        Array.Fill(result, padValue, 0, padding);
        for (var index = 0; index < values.Count; index++)
        {
            result[index + padding] = values[index];
        }

        return result;
    }

    private void RenderValues()
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        UpdateGuides(width, height);

        if (_displayValues.Count == 0)
        {
            HistoryLine.Data = null;
            HistoryArea.Data = null;
            return;
        }

        var (minimum, maximum) = GetScale(_displayValues);
        var usableHeight = Math.Max(1d, height - 4d);
        var points = new Point[_displayValues.Count];

        for (var index = 0; index < points.Length; index++)
        {
            var x = points.Length == 1 ? width : width * index / (points.Length - 1d);
            var normalized = Math.Clamp((_displayValues[index] - minimum) / (maximum - minimum), 0d, 1d);
            points[index] = new Point(x, 2d + ((1d - normalized) * usableHeight));
        }

        HistoryLine.Data = CreateSmoothGeometry(points, closeToBottom: false, height);
        HistoryArea.Data = CreateSmoothGeometry(points, closeToBottom: true, height);
    }

    private (double Minimum, double Maximum) GetScale(IReadOnlyList<double> values)
    {
        if (double.IsFinite(Maximum) && Maximum > 0)
        {
            return (0d, Maximum);
        }

        var minimum = values.Min();
        var maximum = values.Max();
        var range = Math.Max(maximum - minimum, Math.Max(1d, MinimumRange));
        var padding = range * 0.12d;
        return (Math.Max(0d, minimum - padding), maximum + padding);
    }

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

    private void ChartRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ChartCanvas.Width = e.NewSize.Width;
        ChartCanvas.Height = Math.Max(0d, e.NewSize.Height - 10d);
        RenderValues();
    }

    private void ResourceSparkline_Unloaded(object sender, RoutedEventArgs e)
    {
        _animationTimer.Stop();
        DetachCollection();
    }
}
