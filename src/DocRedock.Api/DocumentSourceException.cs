using System.Text;
using System.Text.RegularExpressions;

namespace DocRedock.Api;

/// <summary>Why a document cannot be read at all, named so that a person can act on it: a password-protected Office file,
/// an encrypted PDF, a binary Office file of the old format, or a file that is no document DocRedock reads.</summary>
public enum DocumentSourceProblem { Unsupported, PasswordProtected, EncryptedPdf, LegacyOfficeFormat }

/// <summary>A document that cannot be read, with its <see cref="Problem"/> and the same advice in Japanese
/// (<see cref="JapaneseMessage"/>) for a Japanese user interface.</summary>
public sealed partial class DocumentSourceException(DocumentSourceProblem problem, string message, string japaneseMessage)
    : NotSupportedException(message)
{
    public DocumentSourceProblem Problem { get; } = problem;

    public string JapaneseMessage { get; } = japaneseMessage;

    /// <summary>A short code for machine-readable output, e.g. "password_protected".</summary>
    public string Code => Problem switch
    {
        DocumentSourceProblem.PasswordProtected => "password_protected",
        DocumentSourceProblem.EncryptedPdf => "encrypted_pdf",
        DocumentSourceProblem.LegacyOfficeFormat => "legacy_office_format",
        _ => "unsupported_format",
    };

    // An OLE compound file: what Office writes for a .doc, .xls or .ppt, and for a .docx, .xlsx or .pptx that is
    // encrypted with a password (the package is then stored, encrypted, in its EncryptedPackage stream).
    private static readonly byte[] CompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>The exception for <paramref name="source"/>, a file the format probe did not accept: a password-protected
    /// Office file and a binary Office file of the old format are told apart from other files by their content.</summary>
    public static DocumentSourceException ForUnreadable(string source)
    {
        var name = Path.GetFileName(source);
        var head = ReadHead(source, 64 * 1024);
        if (head.AsSpan().StartsWith(CompoundFileMagic))
        {
            if (ContainsUtf16(head, "EncryptedPackage") || ContainsUtf16(head, "EncryptionInfo"))
                return PasswordProtected(name);
            return new DocumentSourceException(DocumentSourceProblem.LegacyOfficeFormat,
                $"'{name}' is a binary Office file of the old format (.doc, .xls or .ppt). Open it in Microsoft Office or LibreOffice and save it as DOCX, XLSX or PPTX first.",
                $"「{name}」は旧形式（.doc／.xls／.ppt）のOfficeファイルです。Microsoft OfficeまたはLibreOfficeで開き、DOCX／XLSX／PPTX形式で保存し直してから変換してください。");
        }
        return new DocumentSourceException(DocumentSourceProblem.Unsupported,
            $"Document source '{name}' is not a supported or readable DOCX, XLSX, PPTX, or PDF file.",
            $"「{name}」はDocRedockが読めるDOCX／XLSX／PPTX／PDFファイルではないか、壊れています。");
    }

    /// <summary>A password-protected (encrypted) Office file.</summary>
    public static DocumentSourceException PasswordProtected(string name) => new(DocumentSourceProblem.PasswordProtected,
        $"'{name}' is protected with a password (encrypted). Open it in Office, remove the password (File > Info > Protect > Encrypt with Password, clear it), save it, and convert the saved file.",
        $"「{name}」はパスワードで保護（暗号化）されています。Officeで開いてパスワードを解除し（ファイル＞情報＞保護＞パスワードを使用して暗号化 で空欄にする）、保存し直してから変換してください。");

    /// <summary>Whether a PDF is encrypted (its trailer or cross-reference stream names an /Encrypt dictionary). Its text
    /// and drawings cannot be read without decrypting them, which DocRedock does not do. The trailer, or the dictionary
    /// of a cross-reference stream, lies near the end; a linearized file has one near the start too. Content streams,
    /// which could hold the word, are compressed in practice.</summary>
    public static bool IsEncryptedPdf(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[(int)Math.Min(64 * 1024, stream.Length)];
        stream.ReadExactly(head);
        var tailLength = (int)Math.Min(192 * 1024, Math.Max(0, stream.Length - head.Length));
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        stream.ReadExactly(tail);
        return EncryptEntry().IsMatch(Encoding.Latin1.GetString(head)) || EncryptEntry().IsMatch(Encoding.Latin1.GetString(tail));
    }

    /// <summary>An encrypted PDF.</summary>
    public static DocumentSourceException EncryptedPdf(string name) => new(DocumentSourceProblem.EncryptedPdf,
        $"'{name}' is an encrypted PDF (a password to open it, or restrictions on copying and printing). DocRedock does not decrypt PDFs: open it in a PDF viewer and print or export it to a new PDF without security, then convert that file.",
        $"「{name}」は暗号化されたPDF（開くためのパスワード、またはコピー・印刷の制限）です。DocRedockはPDFの暗号を解除しません。PDFビューアーで開き、セキュリティなしの新しいPDFとして印刷・保存してから変換してください。");

    private static byte[] ReadHead(string path, int length)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(length, stream.Length)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool ContainsUtf16(byte[] data, string value) =>
        data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) >= 0;

    [GeneratedRegex(@"/Encrypt\s*(?:\d+\s+\d+\s+R|<<)", RegexOptions.CultureInvariant)]
    private static partial Regex EncryptEntry();
}
