using System.IO.Compression;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using MimeKit;

namespace Mailkeep.Infrastructure;

public sealed record AttachmentExtraction(
    string Status,
    string Text,
    string DetectedMediaType,
    string ExtractorId,
    int ExtractorVersion,
    string? Reason = null,
    bool Truncated = false);

public sealed record AttachmentProbe(string FileName, string DeclaredMediaType, string DetectedMediaType);

public sealed record ExtractionLimits(int MaxInputBytes, int MaxTextCharacters);

public interface IAttachmentTextExtractor
{
    string Id { get; }
    int Version { get; }
    bool CanExtract(AttachmentProbe probe);
    ValueTask<AttachmentExtraction> ExtractAsync(Stream decodedContent, AttachmentProbe probe,
        ExtractionLimits limits, CancellationToken cancellationToken);
}

/// <summary>Pure managed, bounded extraction for the formats in the first attachment-search cut. Parsers
/// receive decoded MIME payloads and never a mail-store path, so they cannot write back to source mail.</summary>
public static class AttachmentTextExtraction
{
    // Bump the coordinator version when the fallback behaviour changes. This invalidates
    // the old "none/image_requires_ocr" rows so an attachment sync can revisit them.
    public const int ExtractorVersion = 2;
    public const int MaxInputBytes = 25 * 1024 * 1024;
    public const int MaxTextCharacters = 1_000_000;
    private const int MaxZipEntries = 10_000;
    private const long MaxZipSelectedBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    public static readonly ExtractionLimits DefaultLimits = new(MaxInputBytes, MaxTextCharacters);

    private static readonly IReadOnlyList<IAttachmentTextExtractor> Extractors =
    [
        new BuiltInExtractor("text", 1,
            probe => probe.DetectedMediaType is "text/plain" or "text/csv" or "text/tab-separated-values"
                or "text/markdown" or "application/json" or "application/xml" or "text/xml",
            (content, _) => DecodeText(content)),
        new BuiltInExtractor("html", 1, probe => probe.DetectedMediaType == "text/html",
            (content, _) => HtmlText(DecodeText(content))),
        new BuiltInExtractor("mime", 1, probe => probe.DetectedMediaType == "message/rfc822",
            (content, _) => EmlText(content)),
        new BuiltInExtractor("ooxml", 1,
            probe => probe.DetectedMediaType.Contains("openxmlformats", StringComparison.Ordinal),
            (content, probe) => probe.DetectedMediaType switch
            {
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => OoxmlText(content, "word/"),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => OoxmlText(content, "xl/"),
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => OoxmlText(content, "ppt/"),
                _ => null
            }),
        new BuiltInExtractor("pdf", 1, probe => probe.DetectedMediaType == "application/pdf",
            (content, _) => PdfText(content)),
        OcrExtractor.Instance
    ];

    public static IReadOnlyList<string> ExtractorIds { get; } = Extractors.Select(extractor => extractor.Id).ToArray();

    public static AttachmentExtraction Extract(byte[] content, string fileName, string declaredMediaType,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > MaxInputBytes)
            return Result("skipped", "", declaredMediaType, "limits", "too_large");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = DetectMediaType(content, extension, declaredMediaType);
        var probe = new AttachmentProbe(fileName, declaredMediaType, mediaType);
        var extractor = Extractors.FirstOrDefault(candidate => candidate.CanExtract(probe));
        try
        {
            if (extractor is null)
            {
                if (IsImage(mediaType, extension)) return Result("needs_ocr", "", mediaType, "none", "image_requires_ocr");
                if (IsArchive(mediaType, extension)) return Result("skipped", "", mediaType, "none", "archive_deferred");
                return Result("skipped", "", mediaType, "none", "unsupported_format");
            }
            using var stream = new MemoryStream(content, writable: false);
            var extraction = extractor.ExtractAsync(stream, probe, DefaultLimits, cancellationToken).AsTask().GetAwaiter().GetResult();
            // The managed PDF reader deliberately declines PDFs without a text layer. Give the
            // OCR extractor a chance only in that case; ordinary text PDFs remain cheap and exact.
            if (extraction.Status == "needs_ocr" && OcrExtractor.CanExtractProbe(probe))
            {
                stream.Position = 0;
                return OcrExtractor.Instance.ExtractAsync(stream, probe, DefaultLimits, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
            }
            return extraction;
        }
        catch (InvalidDataException error)
        {
            return Result("failed", "", mediaType, extractor?.Id ?? "none", Sanitize(error.Message), version: extractor?.Version);
        }
        catch (XmlException error)
        {
            return Result("failed", "", mediaType, extractor?.Id ?? "none", Sanitize(error.Message), version: extractor?.Version);
        }
        catch (RegexMatchTimeoutException)
        {
            return Result("failed", "", mediaType, extractor?.Id ?? "none", "regex_timeout", version: extractor?.Version);
        }
    }

    private static AttachmentExtraction Result(string status, string text, string mediaType, string extractor,
        string? reason = null, bool truncated = false, int? version = null)
        => new(status, text, mediaType, extractor, version ?? VersionFor(extractor), reason, truncated);

    public static int VersionFor(string? extractorId)
        => Extractors.FirstOrDefault(extractor => extractor.Id.Equals(extractorId, StringComparison.Ordinal))?.Version
            ?? ExtractorVersion;

    public static bool HasExtractor(string extractorId)
        => Extractors.Any(extractor => extractor.Id.Equals(extractorId, StringComparison.Ordinal));

    private static string DetectMediaType(byte[] content, string extension, string declared)
    {
        if (content.AsSpan().StartsWith("%PDF-"u8)) return "application/pdf";
        if (content.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            return extension switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => "application/zip"
            };
        }
        return extension switch
        {
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".eml" => "message/rfc822",
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => string.IsNullOrWhiteSpace(declared) ? "application/octet-stream" : declared.ToLowerInvariant()
        };
    }

    private static string DecodeText(byte[] content)
    {
        if (content.Length >= 2 && content[0] == 0xff && content[1] == 0xfe) return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        if (content.Length >= 2 && content[0] == 0xfe && content[1] == 0xff) return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        if (content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf) return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        return new UTF8Encoding(false, false).GetString(content);
    }

    private static string HtmlText(string html)
    {
        html = Regex.Replace(html, "<(script|style)\\b[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        html = Regex.Replace(html, "<[^>]+>", " ", RegexOptions.Singleline, RegexTimeout);
        return WebUtility.HtmlDecode(html);
    }

    private static string EmlText(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var message = MimeMessage.Load(stream);
        var header = string.Join(' ', new[] { message.Subject, message.From.ToString(), message.To.ToString() }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return header + "\n" + (message.TextBody ?? (message.HtmlBody is null ? "" : HtmlText(message.HtmlBody)));
    }

    private static string OoxmlText(byte[] content, string prefix)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxZipEntries) throw new InvalidDataException("ooxml_entry_limit");
        var selected = archive.Entries.Where(entry => IsOoxmlTextPart(entry.FullName, prefix)).ToArray();
        var selectedBytes = selected.Sum(entry => entry.Length);
        if (selectedBytes > MaxZipSelectedBytes || selectedBytes > Math.Max(content.Length * 20L, 1024L * 1024))
            throw new InvalidDataException("ooxml_expansion_limit");
        var result = new StringBuilder();
        foreach (var entry in selected.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            using var entryStream = entry.Open();
            using var reader = XmlReader.Create(entryStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    result.Append(reader.Value).Append(' ');
            }
            result.AppendLine();
            if (result.Length > MaxTextCharacters * 2) break;
        }
        return result.ToString();
    }

    private static bool IsOoxmlTextPart(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return false;
        return prefix switch
        {
            "word/" => name.Contains("document.xml", StringComparison.OrdinalIgnoreCase)
                || name.Contains("header", StringComparison.OrdinalIgnoreCase)
                || name.Contains("footer", StringComparison.OrdinalIgnoreCase),
            "xl/" => name.Contains("sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
                || name.Contains("worksheets/", StringComparison.OrdinalIgnoreCase),
            "ppt/" => name.Contains("slides/slide", StringComparison.OrdinalIgnoreCase)
                || name.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>A conservative text-layer reader for ordinary PDF content streams. It handles literal/hex text
    /// operands and Flate-compressed streams; PDFs needing font-map reconstruction remain empty/needs-ocr.</summary>
    private static string PdfText(byte[] content)
    {
        var chunks = new List<byte[]> { content };
        var raw = Encoding.Latin1.GetString(content);
        var cursor = 0;
        while ((cursor = raw.IndexOf("stream", cursor, StringComparison.Ordinal)) >= 0)
        {
            var dataStart = cursor + 6;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;
            var end = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (end < 0) break;
            var dictionaryStart = Math.Max(0, cursor - 512);
            var dictionary = raw[dictionaryStart..cursor];
            if (dictionary.Contains("FlateDecode", StringComparison.Ordinal))
            {
                try
                {
                    using var compressed = new MemoryStream(content, dataStart, end - dataStart, writable: false);
                    using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
                    using var decoded = new MemoryStream();
                    CopyBounded(inflater, decoded, MaxTextCharacters * 4);
                    chunks.Add(decoded.ToArray());
                }
                catch (InvalidDataException)
                {
                    // Other streams can still contain usable text; one malformed stream does not poison the PDF.
                }
            }
            cursor = end + 9;
        }

        var result = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var source = Encoding.Latin1.GetString(chunk);
            foreach (Match match in Regex.Matches(source, @"\((?<text>(?:\\.|[^\\)])*)\)\s*(?:Tj|'|\x22)", RegexOptions.Singleline, RegexTimeout))
                result.Append(PdfUnescape(match.Groups["text"].Value)).Append(' ');
            foreach (Match array in Regex.Matches(source, @"\[(?<items>.*?)\]\s*TJ", RegexOptions.Singleline, RegexTimeout))
                foreach (Match item in Regex.Matches(array.Groups["items"].Value, @"\((?<text>(?:\\.|[^\\)])*)\)", RegexOptions.None, RegexTimeout))
                    result.Append(PdfUnescape(item.Groups["text"].Value)).Append(' ');
            foreach (Match match in Regex.Matches(source, @"<(?<hex>[0-9A-Fa-f\s]+)>\s*Tj", RegexOptions.None, RegexTimeout))
                result.Append(PdfHex(match.Groups["hex"].Value)).Append(' ');
        }
        return result.ToString();
    }

    private static string PdfUnescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length) { result.Append(value[index]); continue; }
            var next = value[++index];
            if (next is >= '0' and <= '7')
            {
                var octal = next - '0';
                for (var count = 1; count < 3 && index + 1 < value.Length && value[index + 1] is >= '0' and <= '7'; count++)
                    octal = octal * 8 + value[++index] - '0';
                result.Append((char)octal);
            }
            else result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => next });
        }
        return result.ToString();
    }

    private static string PdfHex(string value)
    {
        var compact = new string(value.Where(Uri.IsHexDigit).ToArray());
        if ((compact.Length & 1) != 0) compact += "0";
        var bytes = Convert.FromHexString(compact);
        return bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.Latin1.GetString(bytes);
    }

    private static void CopyBounded(Stream source, Stream destination, int limit)
    {
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, limit - total + 1));
            if (read == 0) return;
            total += read;
            if (total > limit) throw new InvalidDataException("decompressed_text_limit");
            destination.Write(buffer, 0, read);
        }
    }

    private static bool IsImage(string mediaType, string extension) => mediaType.StartsWith("image/", StringComparison.Ordinal)
        || extension is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".gif" or ".tif" or ".tiff" or ".bmp" or ".webp";
    private static bool IsArchive(string mediaType, string extension) => mediaType is "application/zip" or "application/x-7z-compressed"
        or "application/x-rar-compressed" or "application/x-tar" || extension is ".zip" or ".7z" or ".rar" or ".tar";

    private static string Normalize(string value)
    {
        value = value.Replace("\0", " ", StringComparison.Ordinal);
        value = Regex.Replace(value, @"[\p{Cc}&&[^\r\n\t]]", " ", RegexOptions.None, RegexTimeout);
        value = Regex.Replace(value, @"[ \t]+", " ", RegexOptions.None, RegexTimeout);
        value = Regex.Replace(value, @"(?:\r?\n\s*){3,}", "\n\n", RegexOptions.None, RegexTimeout);
        return value.Trim();
    }

    private static string Sanitize(string value) => value.Length <= 160 ? value : value[..160];

    /// <summary>Bounded local OCR. The worker gives this extractor decoded attachment bytes only; the
    /// external tools consume those bytes through standard input and never receive a mailbox or staging path.
    /// Missing tools and OCR failures stay retryable as needs_ocr instead of becoming silently searchable.</summary>
    private sealed class OcrExtractor : IAttachmentTextExtractor
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(8);
        private const int MaxRenderedBytes = 100 * 1024 * 1024;
        private static readonly string DefaultTesseractPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe");
        private static readonly string DefaultMagickPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ImageMagick-7.1.1-Q16-HDRI", "magick.exe");

        public static OcrExtractor Instance { get; } = new();
        public string Id => "ocr";
        public int Version => 1;

        public bool CanExtract(AttachmentProbe probe) => CanExtractProbe(probe);

        public static bool CanExtractProbe(AttachmentProbe probe)
            => IsImage(probe.DetectedMediaType, Path.GetExtension(probe.FileName).ToLowerInvariant())
                || probe.DetectedMediaType == "application/pdf";

        public ValueTask<AttachmentExtraction> ExtractAsync(Stream decodedContent, AttachmentProbe probe,
            ExtractionLimits limits, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var buffer = new MemoryStream();
            decodedContent.CopyTo(buffer);
            if (buffer.Length > limits.MaxInputBytes)
                return new(Result("skipped", "", probe.DetectedMediaType, Id, "too_large", version: Version));

            try
            {
                var ocrInput = buffer.ToArray();
                if (probe.DetectedMediaType == "application/pdf")
                {
                    var magick = ResolveExecutable("MAILKEEP_MAGICK_PATH", DefaultMagickPath, "magick.exe");
                    if (magick is null)
                        return new(Result("needs_ocr", "", probe.DetectedMediaType, Id, "ocr_renderer_unavailable", version: Version));
                    var render = RunBinaryProcess(magick,
                        ["-density", "150", "-", "-background", "white", "-alpha", "remove", "-alpha", "off",
                            "-colorspace", "Gray", "-compress", "LZW", "tiff:-"], ocrInput, cancellationToken);
                    if (!render.Completed || render.ExitCode != 0 || render.OutputLimitExceeded)
                        return new(Result("needs_ocr", "", probe.DetectedMediaType, Id,
                            render.TimedOut ? "ocr_render_timeout" : render.OutputLimitExceeded ? "ocr_render_limit" : "ocr_render_failed", version: Version));
                    ocrInput = render.Stdout;
                }

                var tesseract = ResolveExecutable("MAILKEEP_TESSERACT_PATH", DefaultTesseractPath, "tesseract.exe");
                if (tesseract is null)
                    return new(Result("needs_ocr", "", probe.DetectedMediaType, Id, "ocr_engine_unavailable", version: Version));
                var language = Environment.GetEnvironmentVariable("MAILKEEP_OCR_LANGUAGE");
                if (string.IsNullOrWhiteSpace(language)) language = "eng";
                var result = RunProcess(tesseract, ["stdin", "stdout", "--psm", "3", "-l", language], ocrInput, cancellationToken);
                if (!result.Completed || result.ExitCode != 0)
                    return new(Result("needs_ocr", "", probe.DetectedMediaType, Id,
                        result.TimedOut ? "ocr_timeout" : "ocr_failed", version: Version));

                var text = Normalize(result.Stdout);
                if (text.Length == 0)
                    return new(Result("needs_ocr", "", probe.DetectedMediaType, Id, "ocr_no_text", version: Version));
                var truncated = text.Length > limits.MaxTextCharacters;
                if (truncated) text = text[..limits.MaxTextCharacters];
                return new(Result("indexed", text, probe.DetectedMediaType, Id, truncated: truncated, version: Version));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Win32Exception)
            {
                return new(Result("needs_ocr", "", probe.DetectedMediaType, Id, "ocr_engine_unavailable", version: Version));
            }
            catch (IOException error)
            {
                return new(Result("needs_ocr", "", probe.DetectedMediaType, Id, "ocr_io_error:" + Sanitize(error.Message), version: Version));
            }
        }

        private static string? ResolveExecutable(string variable, string defaultPath, string pathName)
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? configured : null;
            if (File.Exists(defaultPath)) return defaultPath;
            return pathName;
        }

        private static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments, byte[] input,
            CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new Win32Exception("OCR process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessTimeout);
            try
            {
                process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).GetAwaiter().GetResult();
                process.StandardInput.Close();
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessResult(false, true, -1, "", "");
            }
            catch (IOException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new ProcessResult(false, false, -1, "", "");
            }
            return new ProcessResult(true, false, process.ExitCode,
                stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }

        private static BinaryProcessResult RunBinaryProcess(string executable, IReadOnlyList<string> arguments, byte[] input,
            CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new Win32Exception("OCR renderer did not start.");
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxRenderedBytes, process);
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessTimeout);
            try
            {
                process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).GetAwaiter().GetResult();
                process.StandardInput.Close();
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                return new BinaryProcessResult(true, false, false, process.ExitCode,
                    stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
            catch (InvalidDataException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new BinaryProcessResult(false, false, true, -1, [], "");
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new BinaryProcessResult(false, true, false, -1, [], "");
            }
            catch (IOException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new BinaryProcessResult(false, false, false, -1, [], "");
            }
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, Process process)
        {
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory());
                if (read == 0) return output.ToArray();
                if (output.Length > limit - read)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    throw new InvalidDataException("ocr_render_limit");
                }
                output.Write(buffer, 0, read);
            }
        }

        private sealed record ProcessResult(bool Completed, bool TimedOut, int ExitCode, string Stdout, string Stderr);
        private sealed record BinaryProcessResult(bool Completed, bool TimedOut, bool OutputLimitExceeded, int ExitCode,
            byte[] Stdout, string Stderr);
    }

    private sealed class BuiltInExtractor(string id, int version, Func<AttachmentProbe, bool> canExtract,
        Func<byte[], AttachmentProbe, string?> extract) : IAttachmentTextExtractor
    {
        public string Id => id;
        public int Version => version;
        public bool CanExtract(AttachmentProbe probe) => canExtract(probe);

        public async ValueTask<AttachmentExtraction> ExtractAsync(Stream decodedContent, AttachmentProbe probe,
            ExtractionLimits limits, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await decodedContent.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > limits.MaxInputBytes)
                return Result("skipped", "", probe.DetectedMediaType, Id, "too_large", version: Version);
            var text = extract(buffer.ToArray(), probe);
            if (text is null)
                return Result("skipped", "", probe.DetectedMediaType, Id, "unsupported_format", version: Version);
            text = Normalize(text);
            if (text.Length == 0)
                return probe.DetectedMediaType == "application/pdf"
                    ? Result("needs_ocr", "", probe.DetectedMediaType, Id, "no_text_layer", version: Version)
                    : Result("empty", "", probe.DetectedMediaType, Id, version: Version);
            var truncated = text.Length > limits.MaxTextCharacters;
            if (truncated) text = text[..limits.MaxTextCharacters];
            return Result("indexed", text, probe.DetectedMediaType, Id, truncated: truncated, version: Version);
        }
    }
}

public sealed record AttachmentWorkerRequest(string ContentBase64, string FileName, string DeclaredMediaType);

/// <summary>NDJSON worker entry point. Attachment bytes travel inline as bounded base64; no staging path is
/// created for untrusted content, and one malformed document becomes one failed response rather than ending the worker.</summary>
public static class AttachmentWorkerProtocol
{
    public static async Task RunAsync()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            AttachmentExtraction result;
            try
            {
                line = line.TrimStart('\uFEFF');
                var request = JsonSerializer.Deserialize<AttachmentWorkerRequest>(line)
                    ?? throw new InvalidDataException("Worker request was empty.");
                var encoded = request.ContentBase64 ?? throw new InvalidDataException("worker_input_missing");
                if (encoded.Length > ((AttachmentTextExtraction.MaxInputBytes + 2) / 3) * 4)
                    throw new InvalidDataException("worker_input_limit");
                var content = Convert.FromBase64String(encoded);
                if (content.Length > AttachmentTextExtraction.MaxInputBytes)
                    throw new InvalidDataException("worker_input_limit");
                result = AttachmentTextExtraction.Extract(content, request.FileName, request.DeclaredMediaType);
            }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                result = new AttachmentExtraction("failed", "", "application/octet-stream", "worker",
                    AttachmentTextExtraction.ExtractorVersion, error.Message.Length <= 160 ? error.Message : error.Message[..160]);
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result));
            await Console.Out.FlushAsync();
        }
    }
}
