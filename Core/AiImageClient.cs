using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    internal sealed class AiRequestException : Exception
    {
        internal AiRequestException(string message) : base(message) { }
    }

    // Direct provider connections only. No SDK upload helpers, telemetry, logging,
    // shared authorization headers, automatic redirects or automatic paid retries.
    internal sealed class AiImageClient : IDisposable
    {
        internal const int MaxInputBytes = 10 * 1024 * 1024;
        internal const int MaxImageBytes = 32 * 1024 * 1024;
        private const int MaxJsonBytes = 48 * 1024 * 1024;
        private readonly HttpClient _http;
        private readonly TimeSpan _pollDelay;
        internal AiImageClient(HttpMessageHandler? handler = null, TimeSpan? pollDelay = null)
        {
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
                { Timeout = Timeout.InfiniteTimeSpan };
            _pollDelay = pollDelay ?? TimeSpan.FromSeconds(2);
        }
        public void Dispose() => _http.Dispose();

        internal static Uri ValidateEndpoint(string value)
        {
            Uri uri = ValidateHttps(value);
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new AiRequestException("API 주소에는 쿼리나 #을 넣을 수 없습니다. /v1/ 같은 기본 주소를 입력해 주세요.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }
        internal static Uri ValidateHttps(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
                uri.IsLoopback || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
                throw new AiRequestException("공개 HTTPS 주소만 사용할 수 있습니다. 주소에 인증 정보를 넣지 마세요.");
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                byte[] b = ip.GetAddressBytes();
                if (b.Length != 4 || b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) ||
                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127))
                    throw new AiRequestException("내부 네트워크 주소로는 요청하지 않습니다.");
            }
            return uri;
        }
        private static Uri ProviderUrl(string value, Uri origin)
        {
            Uri uri = ValidateHttps(value);
            if (uri.Scheme != origin.Scheme || !uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != origin.Port)
                throw new AiRequestException("제공사가 다른 서버의 인증 주소를 반환해 요청을 중단했습니다.");
            return uri;
        }
        private static string ModelPath(string model, bool pairOnly = false)
        {
            if (!Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9_.-]*(/[A-Za-z0-9][A-Za-z0-9_.-]*)*$") || (pairOnly && model.Count(c => c == '/') != 1))
                throw new AiRequestException("모델 경로가 올바르지 않습니다. 제공사 문서의 모델 ID를 입력해 주세요.");
            return model;
        }
        private static HttpContent Json(object body) => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        private static MultipartFormDataContent Multipart(byte[] png, string imageField, string prompt)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, imageField, "image.png"); form.Add(new StringContent(prompt), "prompt"); return form;
        }
        private static string Text(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        private static JsonElement Member(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;
        private static JsonElement First(JsonElement element) => element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 ? element[0] : default;

        private static HttpRequestMessage Request(HttpMethod method, Uri url, AiProfile profile, string key, HttpContent? body = null)
        {
            ProviderUrl(url.AbsoluteUri, profile.BaseUri);
            var request = new HttpRequestMessage(method, url) { Content = body };
            if (profile.ProviderId == "gemini") request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue(profile.ProviderId == "fal" ? "Key" : "Bearer", key);
            return request;
        }
        private async Task<byte[]> Read(HttpResponseMessage response, int limit, CancellationToken token)
        {
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string reason = status switch
                {
                    401 or 403 => "API 키·모델 접근 권한·계정 인증을 확인해 주세요.",
                    402 => "제공사 계정의 결제 또는 크레딧을 확인해 주세요.",
                    400 or 404 or 422 => "모델 ID, 입력 이미지 규격과 요청 내용을 확인해 주세요.",
                    413 => "이미지가 너무 큽니다. 크기를 줄여 주세요.",
                    429 => "호출 한도 또는 잔액을 확인하고 잠시 후 직접 다시 시도해 주세요.",
                    >= 300 and < 400 => "인증 정보 보호를 위해 API 주소 변경을 자동으로 따라가지 않습니다.",
                    _ => "제공사에서 처리하지 못했습니다. 잠시 후 다시 시도해 주세요."
                };
                // Never include provider response bodies, which may echo credentials/input.
                throw new AiRequestException($"HTTP {status} · {reason}");
            }
            if (response.Content.Headers.ContentLength > limit) throw new AiRequestException("응답 파일이 허용 크기를 넘었습니다.");
            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var result = new MemoryStream();
            byte[] buffer = new byte[81920]; int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > limit) throw new AiRequestException("응답 파일이 허용 크기를 넘었습니다.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
        private async Task<JsonElement> SendJson(HttpRequestMessage request, CancellationToken token)
        {
            using (request)
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                byte[] bytes = await Read(response, MaxJsonBytes, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes); return doc.RootElement.Clone();
            }
        }

        internal async Task<byte[]> EditAsync(AiProfile profile, string key, byte[] png, string prompt, IProgress<string>? progress, CancellationToken cancellation)
        {
            if (png.Length == 0 || png.Length > MaxInputBytes) throw new AiRequestException("전송 이미지는 PNG 기준 10 MB 이하여야 합니다. 이미지 크기를 줄여 주세요.");
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16000) throw new AiRequestException("요청 내용을 1–16,000자로 입력해 주세요.");
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) throw new AiRequestException("설정에 유효한 API 키를 입력해 주세요.");
            if (string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 160 || profile.Model.Any(char.IsControl)) throw new AiRequestException("모델 ID를 입력해 주세요.");
            Uri origin = profile.BaseUri;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(8)); var token = deadline.Token;
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
            Uri? cancelUrl = null;
            try
            {
                progress?.Report("이미지를 전송하고 있습니다…");
                JsonElement result;
                switch (profile.ProviderId)
                {
                    case "openai":
                    case "compatible":
                        var form = Multipart(png, profile.ProviderId == "openai" ? "image[]" : "image", prompt);
                        form.Add(new StringContent(profile.Model), "model");
                        if (profile.ProviderId == "openai") form.Add(new StringContent("png"), "output_format");
                        result = await SendJson(Request(HttpMethod.Post, new Uri(origin, "images/edits"), profile, key, form), token).ConfigureAwait(false);
                        return await OpenAiImage(result, token).ConfigureAwait(false);
                    case "gemini":
                        result = await SendJson(Request(HttpMethod.Post, new Uri(origin, "interactions"), profile, key, Json(new
                        {
                            model = profile.Model, store = false,
                            input = new object[] { new { type = "text", text = prompt }, new { type = "image", data = Convert.ToBase64String(png), mime_type = "image/png" } },
                            response_format = new { type = "image" }
                        })), token).ConfigureAwait(false);
                        var steps = Member(result, "steps");
                        if (steps.ValueKind == JsonValueKind.Array)
                            foreach (var step in steps.EnumerateArray())
                            {
                                if (Text(step, "type") != "model_output") continue;
                                var content = Member(step, "content");
                                if (content.ValueKind != JsonValueKind.Array) continue;
                                foreach (var part in content.EnumerateArray())
                                    if (Text(part, "type") == "image" && Text(part, "data").Length > 0) return Base64(Text(part, "data"));
                            }
                        throw new AiRequestException("Gemini가 이미지를 반환하지 않았습니다. 이미지 모델과 요청 내용을 확인해 주세요.");
                    case "xai":
                        result = await SendJson(Request(HttpMethod.Post, new Uri(origin, "images/edits"), profile, key,
                            Json(new { model = profile.Model, prompt, image = new { url = dataUrl, type = "image_url" } })), token).ConfigureAwait(false);
                        return await OpenAiImage(result, token).ConfigureAwait(false);
                    case "stability":
                        var stable = Multipart(png, "image", prompt);
                        stable.Add(new StringContent("0.7"), "control_strength"); stable.Add(new StringContent("png"), "output_format");
                        using (var request = Request(HttpMethod.Post, new Uri(origin, "stable-image/control/structure"), profile, key, stable))
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                            return await Read(response, MaxImageBytes, token).ConfigureAwait(false);
                        }
                    case "fal":
                        result = await SendJson(Request(HttpMethod.Post, new Uri(origin, ModelPath(profile.Model)), profile, key,
                            Json(new { prompt, image_urls = new[] { dataUrl }, num_images = 1, output_format = "png", sync_mode = true })), token).ConfigureAwait(false);
                        cancelUrl = ProviderUrl(Text(result, "cancel_url"), origin);
                        Uri statusUrl = ProviderUrl(Text(result, "status_url"), origin);
                        Uri responseUrl = ProviderUrl(Text(result, "response_url"), origin);
                        while (true)
                        {
                            progress?.Report("제공사 대기열에서 처리 중… 취소할 수 있습니다.");
                            await Task.Delay(_pollDelay, token).ConfigureAwait(false);
                            var state = await SendJson(Request(HttpMethod.Get, statusUrl, profile, key), token).ConfigureAwait(false);
                            string status = Text(state, "status");
                            if (status == "COMPLETED") break;
                            if (status != "IN_QUEUE" && status != "IN_PROGRESS") throw new AiRequestException("fal.ai 작업이 완료되지 않았습니다.");
                        }
                        result = await SendJson(Request(HttpMethod.Get, responseUrl, profile, key), token).ConfigureAwait(false);
                        cancelUrl = null;
                        return await Download(Text(First(Member(result, "images")), "url"), token).ConfigureAwait(false);
                    case "replicate":
                        var prediction = Request(HttpMethod.Post, new Uri(origin, "models/" + ModelPath(profile.Model, true) + "/predictions"), profile, key,
                            Json(new { input = new { prompt, input_images = new[] { dataUrl }, aspect_ratio = "match_input_image", output_format = "png" } }));
                        prediction.Headers.Add("Prefer", "wait");
                        result = await SendJson(prediction, token).ConfigureAwait(false);
                        while (Text(result, "status") is "starting" or "processing")
                        {
                            var urls = Member(result, "urls");
                            cancelUrl = ProviderUrl(Text(urls, "cancel"), origin);
                            Uri poll = ProviderUrl(Text(urls, "get"), origin);
                            progress?.Report("Replicate에서 이미지 처리 중… 취소할 수 있습니다.");
                            await Task.Delay(_pollDelay, token).ConfigureAwait(false);
                            result = await SendJson(Request(HttpMethod.Get, poll, profile, key), token).ConfigureAwait(false);
                        }
                        cancelUrl = null;
                        if (Text(result, "status") != "succeeded") throw new AiRequestException("Replicate 작업이 실패하거나 취소되었습니다. 모델과 입력 규격을 확인해 주세요.");
                        var output = Member(result, "output");
                        if (output.ValueKind == JsonValueKind.Array) output = First(output);
                        return await Download(output.ValueKind == JsonValueKind.String ? output.GetString()! : "", token).ConfigureAwait(false);
                    default: throw new AiRequestException("지원하지 않는 제공사입니다.");
                }
            }
            catch (OperationCanceledException)
            {
                if (cancelUrl != null)
                {
                    // Best effort remote cancellation; the provider may already have billed the work.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        using var request = Request(profile.ProviderId == "fal" ? HttpMethod.Put : HttpMethod.Post, cancelUrl, profile, key);
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cleanup.Token).ConfigureAwait(false);
                    }
                    catch { /* Do not log request headers or response bodies. */ }
                }
                throw;
            }
        }
        private async Task<byte[]> OpenAiImage(JsonElement result, CancellationToken token)
        {
            var image = First(Member(result, "data")); string encoded = Text(image, "b64_json");
            return encoded.Length > 0 ? Base64(encoded) : await Download(Text(image, "url"), token).ConfigureAwait(false);
        }
        private static byte[] Base64(string data)
        {
            if (data.Length > (long)MaxImageBytes * 4 / 3 + 4) throw new AiRequestException("결과 이미지가 32 MB를 넘었습니다.");
            byte[] bytes = Convert.FromBase64String(data);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new AiRequestException("결과 이미지의 크기가 올바르지 않습니다.");
            return bytes;
        }
        private async Task<byte[]> Download(string address, CancellationToken token)
        {
            if (address.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = address.IndexOf(',');
                if (comma < 0 || !address[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new AiRequestException("결과 이미지 형식이 올바르지 않습니다.");
                return Base64(address[(comma + 1)..]);
            }
            if (string.IsNullOrWhiteSpace(address)) throw new AiRequestException("제공사가 이미지를 반환하지 않았습니다. 요청 내용이나 모델을 확인해 주세요.");
            Uri uri = ValidateHttps(address);
            for (int hop = 0; hop < 4; hop++)
            {
                // Deliberately no Authorization / API key, even on same-origin media downloads.
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location != null)
                { uri = ValidateHttps(new Uri(uri, response.Headers.Location).AbsoluteUri); continue; }
                return await Read(response, MaxImageBytes, token).ConfigureAwait(false);
            }
            throw new AiRequestException("이미지 다운로드 주소가 너무 여러 번 변경되었습니다.");
        }
        internal static BitmapSource DecodeImage(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new AiRequestException("결과 이미지가 허용 크기를 넘었습니다.");
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000)
                throw new AiRequestException("결과 이미지의 해상도가 너무 큽니다 (최대 64 MP).");
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        internal static string ErrorMessage(Exception ex) => ex switch
        {
            AiRequestException => ex.Message,
            System.Security.Cryptography.CryptographicException => "저장한 API 키를 해독하지 못했습니다. 설정에서 키를 다시 입력해 주세요.",
            HttpRequestException => "제공사에 연결하지 못했습니다. 인터넷 연결과 API 주소를 확인해 주세요. 자동 재전송은 하지 않았습니다.",
            _ => "응답을 처리하지 못했습니다. 제공사 모델과 이미지 응답 형식을 확인해 주세요."
        };
    }
}
