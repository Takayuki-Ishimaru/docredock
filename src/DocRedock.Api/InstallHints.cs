namespace DocRedock.Api;

/// <summary>How to install an optional tool on the system DocRedock runs on, as a command to copy, for doctor's advice.</summary>
internal static class InstallHints
{
    /// <summary>" (for example: ...)" with the command that installs Tesseract and its Japanese data.</summary>
    public static string Tesseract => Example(
        windows: "winget install UB-Mannheim.TesseractOCR",
        macOs: "brew install tesseract tesseract-lang",
        linux: "sudo apt install tesseract-ocr tesseract-ocr-jpn  (Fedora: sudo dnf install tesseract tesseract-langpack-jpn)");

    /// <summary>" (for example: ...)" with the command that installs Poppler's pdftoppm.</summary>
    public static string Poppler => Example(
        windows: "winget install oschwartz10612.Poppler",
        macOs: "brew install poppler",
        linux: "sudo apt install poppler-utils  (Fedora: sudo dnf install poppler-utils)");

    private static string Example(string windows, string macOs, string linux) =>
        " (for example: " + (OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? macOs : linux) + ")";
}
