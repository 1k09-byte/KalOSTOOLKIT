using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KalOS.Views;

public sealed partial class MetricRing : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MetricRing), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MetricRing), new PropertyMetadata(double.NaN, OnMetricChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(MetricRing), new PropertyMetadata("%", OnMetricChanged));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MetricRing), new PropertyMetadata(0d));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(MetricRing), new PropertyMetadata(null));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public string ValueText => double.IsNaN(Value) ? "N/A" : $"{Math.Clamp(Value, 0, 100):0}{Unit}";

    public MetricRing()
    {
        InitializeComponent();
    }

    private static void OnMetricChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is MetricRing ring)
        {
            ring.PropertyChanged?.Invoke(ring, new PropertyChangedEventArgs(nameof(ValueText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
