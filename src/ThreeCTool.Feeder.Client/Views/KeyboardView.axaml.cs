using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using ThreeCTool.Feeder.ViewModels;

namespace ThreeCTool.Feeder.Views;

/// <summary>On-screen keyboard; also accepts a physical keyboard (desktop Machine page, or a USB keyboard on the Pi).</summary>
public sealed partial class KeyboardView : UserControl
{
    public KeyboardView()
    {
        InitializeComponent();
        Focusable = true;
        AddHandler(TextInputEvent, OnText, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private KeyboardViewModel? ViewModel => DataContext as KeyboardViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    private void OnText(object? sender, TextInputEventArgs e)
    {
        if (ViewModel is { } viewModel && !string.IsNullOrEmpty(e.Text))
        {
            viewModel.Press(e.Text);
            e.Handled = true;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Back:
                viewModel.Backspace();
                break;
            case Key.Enter:
                viewModel.Accept();
                break;
            case Key.Escape:
                viewModel.Cancel();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
