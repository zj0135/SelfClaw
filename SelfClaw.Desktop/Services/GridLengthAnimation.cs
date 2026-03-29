using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace SelfClaw.Desktop.Services;

public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From),
        typeof(GridLength),
        typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To),
        typeof(GridLength),
        typeof(GridLengthAnimation));

    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction),
        typeof(IEasingFunction),
        typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = From.IsAbsolute ? From.Value : ((GridLength)defaultOriginValue).Value;
        var to = To.IsAbsolute ? To.Value : ((GridLength)defaultDestinationValue).Value;
        var progress = animationClock.CurrentProgress ?? 0d;

        if (EasingFunction is not null)
        {
            progress = EasingFunction.Ease(progress);
        }

        var currentValue = ((to - from) * progress) + from;
        return new GridLength(currentValue, GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore()
        => new GridLengthAnimation();
}