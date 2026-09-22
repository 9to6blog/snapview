using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using SnapView.Capture;

// Exercises the actual overlay and focused toolbar controls without global input,
// capture output, settings changes, or the application's tray/controller startup.
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly Type OverlayType = typeof(OverlayWindow);
    private static readonly PropertyInfo ResultProperty = OverlayType.GetProperty(
        "Result", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load the same resource dictionary without constructing SnapView.App:
        // its queued OnStartup would otherwise start IPC/the real controller.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement dictionary = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"))
            .Descendants(presentation + "ResourceDictionary").First();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());

        foreach (string name in new[] { "BtnMagnet", "BtnEdit", "BtnCopy", "BtnSave", "BtnOk", "BtnCancel" })
            Run("Capture Space with " + name + " focused", "Capture", true,
                overlay => TestSaveFromButton(overlay, name));

        Run("Release without a press inside the overlay", "Capture", true, overlay =>
        {
            var button = FocusButton(overlay, "BtnMagnet");
            KeyEventArgs release = RouteKey(button, Key.Space, false);
            Check("unmatched Space release is consumed", release.Handled);
            Check("unmatched release leaves the overlay open", overlay.IsVisible && Result(overlay) == null);
            RouteKey(button, Key.Space, true);
            RouteKey(button, Key.Space, false);
            Check("a subsequent full Space press saves", Action(overlay) == "SaveOnly");
        });

        Run("Idle Space does not confirm an empty selection", "Capture", false, overlay =>
        {
            overlay.Focus();
            KeyEventArgs down = RouteKey(overlay, Key.Space, true);
            KeyEventArgs up = RouteKey(overlay, Key.Space, false);
            Check("idle Space down and up are consumed", down.Handled && up.Handled);
            Check("idle Space leaves the overlay open", overlay.IsVisible && Result(overlay) == null);
        });

        Run("Record Space retains recording confirmation", "Record", true, overlay =>
        {
            var button = FocusButton(overlay, "BtnMagnet");
            int clicks = 0;
            button.Click += (_, _) => clicks++;
            RouteKey(button, Key.Space, true);
            Check("record Space waits for release", overlay.IsVisible && Result(overlay) == null);
            RouteKey(button, Key.Space, false);
            Check("record Space confirms instead of saving", Action(overlay) == "Confirm");
            Check("record Space does not click the focused magnet", clicks == 0);
        });

        Run("Capture Enter retains configured confirmation", "Capture", true, overlay =>
        {
            overlay.Focus();
            RouteKey(overlay, Key.Enter, true);
            Check("Enter waits for release", overlay.IsVisible && Result(overlay) == null);
            RouteKey(overlay, Key.Enter, false);
            Check("Enter uses configured confirmation", Action(overlay) == "Confirm");
        });

        application.Shutdown();
        Console.WriteLine($"Overlay key regression: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestSaveFromButton(OverlayWindow overlay, string name)
    {
        var button = FocusButton(overlay, name);
        int clicks = 0;
        int closed = 0;
        bool handledWhenClosed = false;
        KeyEventArgs? release = null;
        button.Click += (_, _) => clicks++;
        overlay.Closed += (_, _) =>
        {
            closed++;
            handledWhenClosed = release?.Handled == true;
        };

        // Reproduce the reported preceding magnet action as well as its focus.
        if (name == "BtnMagnet")
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Check("magnet action keeps the overlay open", overlay.IsVisible && Result(overlay) == null);
        }
        int clicksBeforeSpace = clicks;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            KeyEventArgs down = RouteKey(button, Key.Space, true);
            Check($"Space down {repeat + 1} is consumed before ButtonBase", down.Handled);
            Check($"Space down {repeat + 1} waits for release", overlay.IsVisible && Result(overlay) == null);
            Check($"Space down {repeat + 1} does not press the button", !button.IsPressed);
        }

        release = CreateKey(button, Key.Space, false);
        RouteKey(button, release, false);
        Check("Space release is consumed", release.Handled);
        Check("release is already consumed when the overlay closes", handledWhenClosed);
        Check("Space produces a SaveOnly result", Action(overlay) == "SaveOnly");
        Check("overlay closes exactly once", closed == 1 && !overlay.IsVisible);
        Check("Space never reactivates the focused button", clicks == clicksBeforeSpace);
    }

    private static Button FocusButton(OverlayWindow overlay, string name)
    {
        var button = (Button)overlay.FindName(name);
        button.Focus();
        Check(name + " owns keyboard focus", ReferenceEquals(Keyboard.FocusedElement, button));
        return button;
    }

    private static KeyEventArgs CreateKey(UIElement source, Key key, bool down)
    {
        var presentation = PresentationSource.FromVisual(source)
            ?? throw new InvalidOperationException("Test input requires a connected WPF visual.");
        return new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent
        };
    }

    private static KeyEventArgs RouteKey(UIElement source, Key key, bool down)
    {
        KeyEventArgs args = CreateKey(source, key, down);
        RouteKey(source, args, down);
        return args;
    }

    private static void RouteKey(UIElement source, KeyEventArgs args, bool down)
    {
        source.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent;
            source.RaiseEvent(args);
        }
    }

    private static object? Result(OverlayWindow overlay) => ResultProperty.GetValue(overlay);

    private static string? Action(OverlayWindow overlay)
    {
        object? result = Result(overlay);
        return result?.GetType().GetProperty("Action", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(result)?.ToString();
    }

    private static void Run(string title, string purpose, bool selected, Action<OverlayWindow> test)
    {
        Console.WriteLine("\n" + title);
        const int width = 800, height = 450;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        Type purposeType = OverlayType.Assembly.GetType("SnapView.Capture.OverlayPurpose")!;
        var overlay = (OverlayWindow)Activator.CreateInstance(OverlayType,
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { bitmap, new Int32Rect(100, 100, width, height), true, false,
                Enum.Parse(purposeType, purpose), "Regression test", selected }, null)!;
        bool scheduled = false;
        overlay.ContentRendered += (_, _) =>
        {
            if (scheduled) return;
            scheduled = true;
            overlay.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try { test(overlay); }
                catch (Exception ex) { Check("scenario completed without exception", false, ex.ToString()); }
                finally { if (overlay.IsVisible) overlay.Close(); }
            }));
        };
        try { overlay.ShowDialog(); }
        catch (Exception ex) { Check("modal overlay completed without exception", false, ex.ToString()); }
    }

    private static void Check(string name, bool passed, string? details = null)
    {
        if (passed) _passed++; else _failed++;
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")} {name}" + (details == null ? "" : ": " + details));
    }

}
