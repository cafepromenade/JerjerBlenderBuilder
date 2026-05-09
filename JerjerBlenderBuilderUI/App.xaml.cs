using Microsoft.UI.Xaml;

namespace JerjerBlenderBuilderUI;

public partial class App : Application
{
    private static Window? _window;

    public App()
    {
        InitializeComponent();
    }

    public static Window? GetWindow() => _window;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
