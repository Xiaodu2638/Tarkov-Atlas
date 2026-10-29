using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TarkovAtlas.Services;

public sealed class OcrResult
{
    public bool Ok { get; init; }
    public string Text { get; init; } = "";
    public List<string> Lines { get; init; } = new();
    public string Error { get; init; } = "";
}

public sealed class OcrService
{
    /// <summary>Read Scripts/ocr.ps1 out of the assembly (see csproj EmbeddedResource).</summary>
    private static string? LoadScript()
    {
        try
        {
            using var stream = typeof(OcrService).Assembly.GetManifestResourceStream("TarkovAtlas.Scripts.ocr.ps1");
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken token = default)
    {
        if (!File.Exists(imagePath))
            return new OcrResult { Ok = false, Error = "图片不存在: " + imagePath };

        var script = LoadScript();
        if (script is null)
            return new OcrResult { Ok = false, Error = "OCR 脚本缺失（程序集资源）" };

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return new OcrResult { Ok = false, Error = "未找到 Windows PowerShell" };

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        // -EncodedCommand (UTF-16LE base64) instead of -File: nothing on disk is
        // executed, so a swapped Scripts\ocr.ps1 cannot run as administrator.
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.Environment["TARKOVATLAS_OCR_IMAGE"] = imagePath;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new OcrResult { Ok = false, Error = "无法启动 OCR 进程" };

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new OcrResult { Ok = false, Error = "OCR 超时（75 秒）" };
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (stdout.Length == 0)
                return new OcrResult { Ok = false, Error = string.IsNullOrWhiteSpace(stderr) ? "OCR 无输出" : stderr };

            var jsonStart = stdout.IndexOf('{');
            if (jsonStart > 0) stdout = stdout[jsonStart..];
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True)
            {
                var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                var lines = new List<string>();
                if (root.TryGetProperty("lines", out var linesProp) && linesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in linesProp.EnumerateArray())
                    {
                        var value = line.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) lines.Add(value);
                    }
                }
                return new OcrResult { Ok = true, Text = text, Lines = lines };
            }

            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "OCR 失败" : "OCR 失败";
            return new OcrResult { Ok = false, Error = error };
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            return new OcrResult { Ok = false, Error = ex.Message };
        }
    }
}
