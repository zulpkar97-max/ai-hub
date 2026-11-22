using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        private static T AwaitOp<T>(IAsyncOperation<T> op, CancellationToken token)
        {
            using (var ev = new ManualResetEventSlim(false))
            {
                op.Completed = (info, status) => ev.Set();
                if (op.Status == AsyncStatus.Started)
                    ev.Wait(token);
                token.ThrowIfCancellationRequested();
                if (op.Status == AsyncStatus.Error)
                    throw op.ErrorCode ?? new InvalidOperationException("Windows OCR 异步调用失败");
                return op.GetResults();
            }
        }

        public static string RecognizeBitmap(Bitmap bmp, out string engineName)
        {
            return RecognizeBitmap(bmp, out engineName, "auto", CancellationToken.None);
        }

        public static string RecognizeBitmap(Bitmap bmp, out string engineName, string preferredEngine = "auto", CancellationToken cancellationToken = default(CancellationToken))
        {
            if (bmp == null) throw new ArgumentNullException("bmp");
            cancellationToken.ThrowIfCancellationRequested();

            byte[] pngBytes;
            Bitmap prepared = PrepareForOcr(bmp);
            try
            {
                using (var ms = new MemoryStream())
                {
                    prepared.Save(ms, ImageFormat.Png);
                    pngBytes = ms.ToArray();
                }
            }
            finally
            {
                if (!ReferenceEquals(prepared, bmp)) prepared.Dispose();
            }

            string mode = (preferredEngine ?? "auto").Trim().ToLowerInvariant();
            if (mode == "windows")
            {
                engineName = "Windows OCR";
                return RecognizeWithWindows(pngBytes, cancellationToken);
            }

            string text = TryRecognizeWithPaddle(pngBytes, cancellationToken);
            if (text != null)
            {
                engineName = "PaddleOCR（PP-OCRv6）";
                return text;
            }

            if (mode == "paddle")
                throw new InvalidOperationException("本机未检测到可用 PaddleOCR 环境");

            engineName = "Windows OCR（回退）";
            return RecognizeWithWindows(pngBytes, cancellationToken);
        }

        /// <summary>
        /// Small crops (&lt;180w or &lt;80h) are 2x bicubic upscaled with 16px white padding for higher OCR accuracy.
        /// </summary>
        private static Bitmap PrepareForOcr(Bitmap source)
        {
            if (source.Width >= 180 && source.Height >= 80) return source;

            const int pad = 16;
            int scaledW = source.Width * 2;
            int scaledH = source.Height * 2;
            var dst = new Bitmap(scaledW + pad * 2, scaledH + pad * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(pad, pad, scaledW, scaledH),
                    new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return dst;
        }

        private static string TryRecognizeWithPaddle(byte[] pngBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
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
                    using (token.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                    }))
                    {
                        process.BeginErrorReadLine();
                        Task<string> output = process.StandardOutput.ReadToEndAsync();
                        var input = SendImageAsync(process.StandardInput.BaseStream, pngBytes);
                        if (!process.WaitForExit(45000))
                        {
                            try { if (!process.HasExited) process.Kill(); } catch { }
                            token.ThrowIfCancellationRequested();
                            return null;
                        }

                        token.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0 || !input.GetAwaiter().GetResult()) return null;
                        return (output.GetAwaiter().GetResult() ?? "").Trim();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
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

        private static string RecognizeWithWindows(byte[] pngBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var ras = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pngBytes);
                    AwaitOp(writer.StoreAsync(), token);
                    AwaitOp(writer.FlushAsync(), token);
                    writer.DetachStream();
                }

                ras.Seek(0);
                BitmapDecoder decoder = AwaitOp(BitmapDecoder.CreateAsync(ras), token);
                SoftwareBitmap softBmp = AwaitOp(decoder.GetSoftwareBitmapAsync(), token);
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

                    OcrResult res = AwaitOp(engine.RecognizeAsync(softBmp), token);
                    token.ThrowIfCancellationRequested();
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
