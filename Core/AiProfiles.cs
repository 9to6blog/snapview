using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapView.Core
{
    internal sealed record AiProvider(string Id, string Name, string Endpoint, string[] Models, string Note, string Documentation)
    {
        public override string ToString() => Name;
        internal static readonly AiProvider[] All =
        {
            new("openai", "OpenAI", "https://api.openai.com/v1/", new[] { "gpt-image-2.5-sunburst", "gpt-image-2.5-flare", "gpt-image-2", "gpt-image-1.5" },
                "이미지 편집 · OpenAI API 키가 필요합니다. ChatGPT 구독과 별도 과금됩니다.", "https://developers.openai.com/api/docs/guides/image-generation"),
            new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/", new[] { "gemini-3.1-flash-image", "gemini-3-pro-image", "gemini-2.5-flash-image" },
                "이미지 편집 · Gemini API 키 · Interactions API로 전송하며 store=false를 지정합니다.", "https://ai.google.dev/gemini-api/docs/image-generation"),
            new("xai", "xAI Grok", "https://api.x.ai/v1/", new[] { "grok-imagine-image-2.0" },
                "이미지 편집 · xAI API 키가 필요합니다.", "https://docs.x.ai/developers/model-capabilities/images/editing"),
            new("stability", "Stability AI", "https://api.stability.ai/v2beta/", new[] { "stable-image/control/structure" },
                "구조 유지 변환 · 원본의 구도를 유지하면서 프롬프트에 맞춰 재생성합니다. 가로세로 비율 1:2.5–2.5:1, 각 변 64 px 이상, 최대 약 9 MP.", "https://platform.stability.ai/docs/api-reference"),
            new("fal", "fal.ai", "https://queue.fal.run/", new[] { "fal-ai/nano-banana-2/edit" },
                "Nano Banana 2 편집 · image_urls 입력 모델용입니다. 다른 입력 규격의 모델은 지원하지 않습니다.", "https://fal.ai/models/fal-ai/nano-banana-2/edit/api"),
            new("replicate", "Replicate", "https://api.replicate.com/v1/", new[] { "black-forest-labs/flux-2-pro" },
                "FLUX.2 Pro 편집 · input_images 입력을 받는 공식 모델(owner/name)용입니다.", "https://replicate.com/black-forest-labs/flux-2-pro"),
            new("compatible", "OpenAI 호환 API", "", new[] { "" },
                "직접 지정한 HTTPS 서버에 키와 이미지를 보냅니다. multipart /images/edits 및 data[].b64_json 또는 URL 응답을 지원해야 합니다.", "https://developers.openai.com/api/reference/resources/images/methods/edit")
        };
        internal static AiProvider Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
    }

    internal sealed class AiProfile
    {
        public string ProviderId { get; set; } = "openai";
        public string Model { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string ProtectedKey { get; set; } = "";
        internal AiProfile Clone() => (AiProfile)MemberwiseClone();
        internal AiProvider Provider => AiProvider.Find(ProviderId);
        internal Uri BaseUri => AiImageClient.ValidateEndpoint(ProviderId == "compatible" ? Endpoint : Provider.Endpoint);
        internal string KeyScope => ProviderId + "|" + BaseUri.AbsoluteUri;
        internal string ReadKey() => string.IsNullOrEmpty(ProtectedKey) ? "" : LocalSecret.Unprotect(ProtectedKey, KeyScope);
        internal void SetKey(string key) => ProtectedKey = string.IsNullOrEmpty(key) ? "" : LocalSecret.Protect(key, KeyScope);
    }

    internal sealed class AiPreferences
    {
        public string SelectedProvider { get; set; } = "openai";
        public List<AiProfile> Profiles { get; set; } = new();
        internal AiProfile Get(string id)
        {
            var p = Profiles.FirstOrDefault(p => p.ProviderId == id);
            if (p != null) return p;
            var provider = AiProvider.Find(id);
            p = new AiProfile { ProviderId = provider.Id, Model = provider.Models[0], Endpoint = provider.Endpoint };
            Profiles.Add(p); return p;
        }
    }

    // Kept outside roaming settings, projects, recovery files and source control.
    internal sealed class AiSettingsStore
    {
        internal string FilePath { get; }
        internal AiSettingsStore(string? directory = null) => FilePath = Path.Combine(directory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapView"), "ai-settings.json");
        internal AiPreferences Load()
        {
            if (!File.Exists(FilePath)) return new AiPreferences();
            var result = JsonSerializer.Deserialize<AiPreferences>(File.ReadAllText(FilePath))
                ?? throw new IOException("AI 설정 파일을 읽을 수 없습니다.");
            if (result.Profiles == null || result.Profiles.Any(p => p == null)) throw new IOException("AI 설정 파일 형식이 잘못되었습니다.");
            return result;
        }
        internal void Save(AiPreferences preferences)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temp, FilePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    // DPAPI CurrentUser: ciphertext can only be decrypted by this Windows account.
    // No LocalMachine flag; UI prompts are forbidden. Plaintext buffers are zeroed.
    internal static class LocalSecret
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static byte[] Transform(byte[] data, string scope, bool protect)
        {
            byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("SnapView.AI.v1|" + scope));
            var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            var entropy = new Blob { Size = salt.Length, Data = Marshal.AllocHGlobal(salt.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length); Marshal.Copy(salt, 0, entropy.Data, salt.Length);
                bool ok = protect ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("API 키를 암호화하거나 해독하지 못했습니다. 이 Windows 계정에서 키를 다시 입력해 주세요.");
                byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
                Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
                if (output.Data != IntPtr.Zero)
                {
                    for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                    LocalFree(output.Data);
                }
                CryptographicOperations.ZeroMemory(salt);
            }
        }
        internal static string Protect(string value, string scope)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            try { return Convert.ToBase64String(Transform(bytes, scope, true)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        internal static string Unprotect(string value, string scope)
        {
            byte[] bytes = Transform(Convert.FromBase64String(value), scope, false);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
