using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CodexLauncher
{
    public static class OcrService
    {
        // Strip spaces inserted by Windows OCR between adjacent CJK characters or CJK punctuation
        private static readonly Regex CjkSpaceRegex = new Regex(
            @"(?<=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])\s+(?=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])",
            RegexOptions.Compiled);

        private static T AwaitOp<T>(IAsyncOperation<T> op)
        {
            using (var ev = new ManualResetEventSlim(false))
            {
                op.Completed = (info, status) => ev.Set();
                if (op.Status == AsyncStatus.Started)
                    ev.Wait();
                if (op.Status == AsyncStatus.Error)
                    throw op.ErrorCode ?? new InvalidOperationException("Windows OCR 异步调用失败");
                return op.GetResults();
            }
        }

        public static string RecognizeBitmap(Bitmap bmp, out string engineName)
        {
            if (bmp == null) throw new ArgumentNullException("bmp");

            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            string text = TryRecognizeWithPaddle(pngBytes);
            if (!string.IsNullOrWhiteSpace(text))
            {
                engineName = "PaddleOCR（PP-OCRv6）";
                return text;
            }

            engineName = "Windows OCR（回退）";
            return RecognizeWithWindows(pngBytes);
        }

        private static string TryRecognizeWithPaddle(byte[] pngBytes)
        {
            string environment = Path.Combine(CoreService.DataDir, "ocr", "rapidocr-3.10.0");
            string python = Path.Combine(environment, "Scripts", "python.exe");
            string script = Path.Combine(CoreService.BaseDir, "资源", "OCR", "识别.py");
            if (!File.Exists(python) || !File.Exists(script)) return null;

            var start = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-I -B \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                using (var process = Process.Start(start))
                {
                    if (process == null) return null;
                    process.BeginErrorReadLine();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    var input = SendImageAsync(process.StandardInput.BaseStream, pngBytes);
                    if (!process.WaitForExit(45000))
                    {
                        if (!process.HasExited) process.Kill();
                        return null;
                    }

                    if (process.ExitCode != 0 || !input.GetAwaiter().GetResult()) return null;
                    return output.GetAwaiter().GetResult().Trim();
                }
            }
            catch (Win32Exception) { return null; }
            catch (IOException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static async Task<bool> SendImageAsync(Stream input, byte[] pngBytes)
        {
            try
            {
                using (input) await input.WriteAsync(pngBytes, 0, pngBytes.Length).ConfigureAwait(false);
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        private static string RecognizeWithWindows(byte[] pngBytes)
        {
            using (var ras = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pngBytes);
                    AwaitOp(writer.StoreAsync());
                    AwaitOp(writer.FlushAsync());
                    writer.DetachStream();
                }

                ras.Seek(0);
                BitmapDecoder decoder = AwaitOp(BitmapDecoder.CreateAsync(ras));
                SoftwareBitmap softBmp = AwaitOp(decoder.GetSoftwareBitmapAsync());
                try
                {
                    OcrEngine engine = null;
                    try { engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN")); } catch { }
                    if (engine == null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (engine == null)
                    {
                        var langs = OcrEngine.AvailableRecognizerLanguages;
                        if (langs != null && langs.Count > 0)
                            engine = OcrEngine.TryCreateFromLanguage(langs[0]);
                    }
                    if (engine == null)
                        throw new InvalidOperationException("当前系统未安装可用的 Windows OCR 语言包");

                    OcrResult res = AwaitOp(engine.RecognizeAsync(softBmp));
                    if (res == null || res.Lines == null || res.Lines.Count == 0)
                        return "";

                    var sb = new StringBuilder();
                    for (int i = 0; i < res.Lines.Count; i++)
                    {
                        string line = res.Lines[i].Text ?? "";
                        line = CjkSpaceRegex.Replace(line, "").Trim();
                        if (i > 0) sb.AppendLine();
                        sb.Append(line);
                    }
                    return sb.ToString().Trim();
                }
                finally
                {
                    if (softBmp != null) softBmp.Dispose();
                }
            }
        }
    }
}
