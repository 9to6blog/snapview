using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

// All network calls use an in-memory handler. Never read production settings or keys.
static class Program
{
    const string Key = "test-only-not-a-real-credential";
    const string Prompt = "배경을 파스텔 톤으로 바꿔 주세요";
    static int checks;
    static byte[] image = Array.Empty<byte>();
    static string B64 => Convert.ToBase64String(image);
    static void Check(string name, bool ok) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static AiProfile Profile(string id) { var p = new AiPreferences().Get(id); if (id == "compatible") { p.Endpoint = "https://custom.example/v1/"; p.Model = "image-model"; } return p; }
    static HttpResponseMessage Json(object obj) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json") };
    static HttpResponseMessage Binary() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(image) };
    static HttpResponseMessage ImageResult() => Json(new { data = new[] { new { b64_json = B64 } } });
    sealed class Fake : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> action;
        internal int Calls;
        internal Fake(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return action(request, ++Calls, token); }
    }
    sealed class ImmediateProgress : IProgress<string>
    {
        readonly Action<string> action;
        internal ImmediateProgress(Action<string> action) => this.action = action;
        public void Report(string value) => action(value);
    }
    static async Task<byte[]> Run(string id, Fake handler, CancellationToken token = default, string? model = null, IProgress<string>? progress = null)
    {
        using var client = new AiImageClient(handler, TimeSpan.Zero); var profile = Profile(id);
        if (model != null) profile.Model = model;
        return await client.EditAsync(profile, Key, image, Prompt, progress, token);
    }
    static void Auth(HttpRequestMessage request, string scheme = "Bearer")
    {
        Check("authorization is attached only as a request header", request.Headers.Authorization?.Scheme == scheme && request.Headers.Authorization.Parameter == Key && !request.RequestUri!.AbsoluteUri.Contains(Key));
    }
    static async Task Failure(string name, Func<Task> action, string? expected = null)
    {
        try { await action(); throw new Exception("Expected rejection: " + name); }
        catch (AiRequestException ex) { Check(name, !ex.Message.Contains(Key) && (expected == null || ex.Message.Contains(expected))); }
    }
    static void Keys(string directory)
    {
        var store = new AiSettingsStore(directory); var prefs = store.Load(); var p = prefs.Get("openai");
        p.SetKey(Key); string first = p.ProtectedKey; p.SetKey(Key);
        Check("DPAPI uses randomized ciphertext", first != p.ProtectedKey && p.ProtectedKey != Key);
        store.Save(prefs); string file = File.ReadAllText(store.FilePath);
        Check("settings contain ciphertext, not plaintext or a base64 key", !file.Contains(Key) && !file.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Key))));
        Check("same Windows user can round-trip encrypted key", store.Load().Get("openai").ReadKey() == Key);
        var swapped = p.Clone(); swapped.ProviderId = "gemini";
        try { swapped.ReadKey(); throw new Exception("scope not bound"); }
        catch (CryptographicException) { Check("key cannot be transplanted to another provider", true); }
        var custom = Profile("compatible"); custom.SetKey(Key); custom.Endpoint = "https://different.example/v1/";
        try { custom.ReadKey(); throw new Exception("host not bound"); }
        catch (CryptographicException) { Check("key cannot be reused after custom endpoint change", true); }
        p.SetKey(""); store.Save(prefs);
        Check("key deletion persists", store.Load().Get("openai").ReadKey() == "" && !File.Exists(store.FilePath + ".tmp"));
    }
    static async Task Providers()
    {
        foreach (string id in new[] { "openai", "compatible" })
        {
            var fake = new Fake(async (r, n, ct) =>
            {
                Auth(r); string body = await r.Content!.ReadAsStringAsync(ct);
                Check(id + " posts multipart image edits to exact endpoint", r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/v1/images/edits" && r.Content is MultipartFormDataContent);
                Check(id + " sends image bytes with a neutral filename", body.Contains("image.png") && body.Contains(Prompt) && !body.Contains("C:\\") && !body.Contains(Key));
                var parts = ((MultipartFormDataContent)r.Content).ToArray();
                Check(id + " uses documented image field", parts[0].Headers.ContentDisposition!.Name!.Trim('"') == (id == "openai" ? "image[]" : "image"));
                return ImageResult();
            });
            Check(id + " reads base64 result", (await Run(id, fake)).SequenceEqual(image));
        }
        var gemini = new Fake(async (r, n, ct) =>
        {
            Check("Gemini uses header key and Interactions endpoint", r.Headers.GetValues("x-goog-api-key").Single() == Key && r.Headers.Authorization == null && r.RequestUri!.AbsolutePath == "/v1beta/interactions" && r.RequestUri.Query == "");
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)); var body = doc.RootElement;
            Check("Gemini disables interaction storage and sends inline image", !body.GetProperty("store").GetBoolean() && body.GetProperty("input")[1].GetProperty("data").GetString() == B64 && body.GetProperty("response_format").GetProperty("type").GetString() == "image");
            return Json(new { steps = new object[] { new { type = "user_input", content = new[] { new { type = "image", data = "wrong-source" } } }, new { type = "model_output", content = new[] { new { type = "image", data = B64, mime_type = "image/png" } } } } });
        });
        Check("Gemini reads generated output and ignores echoed source", (await Run("gemini", gemini)).SequenceEqual(image));
        var xai = new Fake(async (r, n, ct) =>
        {
            if (n == 1)
            {
                Auth(r); using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                Check("xAI uses JSON image_url input", doc.RootElement.GetProperty("image").GetProperty("url").GetString() == "data:image/png;base64," + B64 && r.Content.Headers.ContentType!.MediaType == "application/json");
                return Json(new { data = new[] { new { url = "https://media.example/result" } } });
            }
            Check("media request never receives provider key or cookies", r.Headers.Authorization == null && !r.Headers.Contains("x-goog-api-key") && !r.Headers.Contains("Cookie"));
            if (n == 2) { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://cdn.example/result.png"); return redirect; }
            return Binary();
        });
        Check("xAI follows HTTPS media redirects without credentials", (await Run("xai", xai)).SequenceEqual(image) && xai.Calls == 3);
        var stable = new Fake(async (r, n, ct) =>
        {
            Auth(r); string body = await r.Content!.ReadAsStringAsync(ct);
            Check("Stability structure endpoint and binary accept", r.RequestUri!.AbsolutePath == "/v2beta/stable-image/control/structure" && r.Headers.Accept.Single().MediaType == "image/*");
            Check("Stability controls and PNG output are multipart fields", body.Contains("control_strength") && body.Contains("0.7") && body.Contains("output_format"));
            return Binary();
        });
        Check("Stability reads raw binary image", (await Run("stability", stable)).SequenceEqual(image));
        var fal = new Fake(async (r, n, ct) =>
        {
            Auth(r, "Key"); Check("fal requests stay on its queue host", r.RequestUri!.Host == "queue.fal.run");
            if (n == 1)
            {
                using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                Check("fal sends data URI without uploading to storage", doc.RootElement.GetProperty("image_urls")[0].GetString() == "data:image/png;base64," + B64 && doc.RootElement.GetProperty("sync_mode").GetBoolean());
                return Json(new { cancel_url = "https://queue.fal.run/model/requests/1/cancel", status_url = "https://queue.fal.run/model/requests/1/status", response_url = "https://queue.fal.run/model/requests/1" });
            }
            if (n == 2) return Json(new { status = "IN_PROGRESS" });
            if (n == 3) return Json(new { status = "COMPLETED" });
            return Json(new { images = new[] { new { url = "data:image/png;base64," + B64 } } });
        });
        Check("fal polls and reads data URI response", (await Run("fal", fal)).SequenceEqual(image) && fal.Calls == 4);
        var replicate = new Fake(async (r, n, ct) =>
        {
            if (n < 4) Auth(r); else Check("Replicate CDN gets no key", r.Headers.Authorization == null);
            if (n == 1)
            {
                using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                Check("Replicate uses official model predictions endpoint", r.RequestUri!.AbsolutePath == "/v1/models/black-forest-labs/flux-2-pro/predictions" && r.Headers.GetValues("Prefer").Single() == "wait");
                Check("Replicate sends inline input_images", doc.RootElement.GetProperty("input").GetProperty("input_images")[0].GetString() == "data:image/png;base64," + B64);
            }
            if (n < 3) return Json(new { status = n == 1 ? "starting" : "processing", urls = new { get = "https://api.replicate.com/v1/predictions/1", cancel = "https://api.replicate.com/v1/predictions/1/cancel" } });
            return n == 3 ? Json(new { status = "succeeded", output = new[] { "https://replicate.delivery/result.png" } }) : Binary();
        });
        Check("Replicate polls until successful and downloads output", (await Run("replicate", replicate)).SequenceEqual(image));
    }
    static async Task SecurityAndCancellation()
    {
        foreach (string endpoint in new[] { "http://api.example/v1", "https://user:password@api.example/", "https://api.example/?key=secret", "https://127.0.0.1/", "https://192.168.0.1/", "https://service.local/" })
            await Failure("reject unsafe API endpoint", () => { AiImageClient.ValidateEndpoint(endpoint); return Task.CompletedTask; });
        var redirect = new Fake((r, n, ct) => { var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StringContent(Key) }; response.Headers.Location = new Uri("https://other.example/"); return Task.FromResult(response); });
        await Failure("API redirect is refused without exposing key", () => Run("openai", redirect), "307");
        Check("API redirect never receives another request", redirect.Calls == 1);
        var polling = new Fake((r, n, ct) => Task.FromResult(Json(new { cancel_url = "https://queue.fal.run/cancel", status_url = "https://evil.example/status", response_url = "https://queue.fal.run/result" })));
        await Failure("cross-origin polling URL is refused", () => Run("fal", polling));
        Check("malicious poll target never receives a key", polling.Calls == 1);
        var rateLimit = new Fake((r, n, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Key) }));
        await Failure("errors are sanitized and paid requests not retried", () => Run("openai", rateLimit), "429");
        Check("rate limited call is not duplicated", rateLimit.Calls == 1);
        var empty = new Fake((r, n, ct) => Task.FromResult(Json(new { steps = new[] { new { type = "model_output", content = new[] { new { type = "text", text = "no image" } } } } })));
        await Failure("text-only model answer is not treated as image", () => Run("gemini", empty));
        var badMedia = new Fake((r, n, ct) => Task.FromResult(Json(new { data = new[] { new { url = "http://cdn.example/insecure.png" } } })));
        await Failure("insecure result download is blocked", () => Run("xai", badMedia));
        var never = new Fake((r, n, ct) => throw new Exception("Must not send"));
        await Failure("model path injection is blocked before HTTP", () => Run("replicate", never, model: "../evil"));
        using (var client = new AiImageClient(never))
            await Failure("oversize image is rejected before HTTP", () => client.EditAsync(Profile("openai"), Key, new byte[AiImageClient.MaxInputBytes + 1], Prompt, null, default));
        foreach (string id in new[] { "fal", "replicate" })
        {
            using var canceled = new CancellationTokenSource();
            string host = id == "fal" ? "https://queue.fal.run" : "https://api.replicate.com";
            var fake = new Fake((r, n, ct) =>
            {
                if (n == 1)
                {
                    return Task.FromResult(id == "fal" ? Json(new { status_url = host + "/status", response_url = host + "/result", cancel_url = host + "/cancel" })
                        : Json(new { status = "processing", urls = new { get = host + "/status", cancel = host + "/cancel" } }));
                }
                Check(id + " cancellation uses exact provider method and host", r.Method == (id == "fal" ? HttpMethod.Put : HttpMethod.Post) && r.RequestUri!.AbsoluteUri == host + "/cancel");
                Auth(r, id == "fal" ? "Key" : "Bearer"); return Task.FromResult(Json(new { status = "canceled" }));
            });
            int progressCalls = 0;
            var progress = new ImmediateProgress(_ => { if (++progressCalls == 2) canceled.Cancel(); });
            try { await Run(id, fake, canceled.Token, progress: progress); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(id + " cancellation stops poll and requests remote cancellation", fake.Calls == 2); }
        }
    }
    [STAThread]
    static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SnapViewAiTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var buffer = new MemoryStream(); encoder.Save(buffer); image = buffer.ToArray();
            Keys(directory); Providers().GetAwaiter().GetResult(); SecurityAndCancellation().GetAwaiter().GetResult();
            var decoded = AiImageClient.DecodeImage(image);
            Check("result image is frozen and independent from closed stream", decoded.IsFrozen && decoded.PixelWidth == 2 && decoded.PixelHeight == 2);
            byte[] pixels = new byte[16]; decoded.CopyPixels(pixels, 8, 0); Check("decoded pixels remain readable", pixels.Any(b => b != 0));
            Console.WriteLine($"RESULT: {checks} AI checks passed (mock HTTP, no live keys or paid calls)"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(directory), root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("SnapViewAiTests-", StringComparison.Ordinal) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
