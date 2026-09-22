using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static partial class Program
{
    static object Create(string name, params object?[] args) => Activator.CreateInstance(AppAssembly.GetType(name)!, Members, null, args, null)!;
    static void LayoutDialog(Window w)
    {
        var content = (FrameworkElement)w.Content;
        content.Measure(new Size(w.Width - 20, w.Height - 40));
        content.Arrange(new Rect(0, 0, w.Width - 20, w.Height - 40)); content.UpdateLayout();
    }
    static void TestAiDialogs(string folder, string? preview)
    {
        object store = Create("SnapView.Core.AiSettingsStore", folder);
        var settings = (Window)Create("SnapView.Prefs.AiSettingsWindow", store, null);
        var provider = (ComboBox)settings.FindName("ProviderBox");
        var key = (PasswordBox)settings.FindName("KeyBox");
        Check("AI settings initially show no secret", key.Password.Length == 0);
        key.Password = "test-only-openai-key";
        provider.SelectedIndex = 1;
        Check("switching provider clears password field", key.Password.Length == 0);
        key.Password = "test-only-gemini-key";
        Call(settings, "CaptureDraft");
        object preferences = Field(settings, "_preferences")!;
        Call(store, "Save", preferences);
        provider.SelectedIndex = 0;
        Check("stored API key is never displayed in the password field", key.Password.Length == 0);
        LayoutDialog(settings);
        var model = (ComboBox)settings.FindName("ModelBox"); model.ApplyTemplate();
        Check("model combo has a working editable text part", model.Template.FindName("PART_EditableTextBox", model) is TextBox editable && editable.Visibility == Visibility.Visible);
        model.Text = "future-image-model"; Call(settings, "CaptureDraft");
        Check("custom model ID can be entered", (string)Prop(Field(settings, "_current")!, "Model")! == "future-image-model");
        provider.SelectedIndex = 6;
        ((TextBox)settings.FindName("EndpointBox")).Text = "https://custom.example/v1/";
        model.Text = "image-model"; key.Password = "test-only-custom-key"; Call(settings, "CaptureDraft");
        ((TextBox)settings.FindName("EndpointBox")).Text = "https://another.example/v1/";
        provider.SelectedIndex = 0;
        Check("changing custom host requires entering its own key", provider.SelectedIndex == 6 && ((TextBlock)settings.FindName("Status")).Text.Contains("다시 입력"));
        key.Password = "test-only-other-key"; provider.SelectedIndex = 0;
        Check("new host is accepted with a newly entered key", provider.SelectedIndex == 0);
        if (preview != null) { LayoutDialog(settings); RenderPreview(settings, Path.Combine(Path.GetDirectoryName(preview)!, "ai-settings.png")); }
        settings.Close();

        var editor = Editor(folder); var original = (BitmapSource)Field(editor, "_image")!;
        var dialog = (Window)Create("SnapView.Editor.AiImageDialog", original, new Int32Rect(90, 70, 240, 180), store);
        var source = (ComboBox)dialog.FindName("SourceBox");
        var input = (BitmapSource)((Image)dialog.FindName("SourcePreview")).Source;
        Check("AI dialog defaults to the selected region", source.SelectedIndex == 1 && input.PixelWidth == 240 && input.PixelHeight == 180);
        Check("stored provider key enables explicit send button", ((Button)dialog.FindName("SendButton")).IsEnabled);
        Check("AI result cannot be applied before a response", !((Button)dialog.FindName("ApplyButton")).IsEnabled && Prop(dialog, "Result") == null);
        source.SelectedIndex = 0;
        Check("whole-image preview matches actual canvas input", ReferenceEquals(((Image)dialog.FindName("SourcePreview")).Source, original));
        ((ComboBox)dialog.FindName("ProviderBox")).SelectedIndex = 2;
        Check("provider without a key cannot send", !((Button)dialog.FindName("SendButton")).IsEnabled);
        ((ComboBox)dialog.FindName("ProviderBox")).SelectedIndex = 0;
        ((TextBox)dialog.FindName("PromptBox")).Text = "배경을 부드러운 파스텔 톤으로 바꾸고 구도는 유지해 주세요.";
        if (preview != null) { LayoutDialog(dialog); RenderPreview(dialog, preview); }
        dialog.Close();

        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.PeachPuff, null, new Rect(0, 0, 200, 100));
        var result = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze();
        Call(editor, "AddAiResult", result, new Rect(100, 80, 300, 200), "Test provider");
        Check("AI result adds an editable image layer", Items(editor).Count == 1 && Items(editor)[0]!.GetType().Name == "ImageAnnotation");
        Check("AI result preserves background layer zero", ReferenceEquals(Field(editor, "_image"), original));
        Check("AI result fits selected area without distortion", (Rect)Prop(Items(editor)[0]!, "Bounds")! == new Rect(100, 105, 300, 150));
        Check("AI result marks editor dirty", (bool)Field(editor, "_dirty")!);
        Call(editor, "Undo"); Check("AI result insertion is undoable", Items(editor).Count == 0 && ReferenceEquals(Field(editor, "_image"), original));
        Call(editor, "Redo"); Check("AI result redo restores image layer", Items(editor).Count == 1);
    }
}
