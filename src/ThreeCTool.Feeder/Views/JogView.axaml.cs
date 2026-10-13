using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using ThreeCTool.Feeder.ViewModels;

namespace ThreeCTool.Feeder.Views;

public sealed partial class JogView : UserControl
{
    public JogView()
    {
        InitializeComponent();

        // Press-and-hold jogging: buttons handle their own pointer events, so listen in the tunnel phase.
        Pad.AddHandler(PointerPressedEvent, OnPadPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Pad.AddHandler(PointerReleasedEvent, OnPadReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Pad.AddHandler(PointerCaptureLostEvent, OnPadCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private JogViewModel? ViewModel => DataContext as JogViewModel;

    private void OnPadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { Continuous: true } viewModel && e.Source is Visual source && FindJogButton(source) is { Tag: string direction })
        {
            viewModel.StartContinuous(direction);
        }
    }

    private void OnPadReleased(object? sender, PointerReleasedEventArgs e) => ViewModel?.StopContinuous();

    private void OnPadCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ViewModel?.StopContinuous();

    private static Button? FindJogButton(Visual source) =>
        source.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("jog"));
}
