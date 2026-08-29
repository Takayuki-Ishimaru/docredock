using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Api;

/// <summary>Converts adapter warning strings to stable diagnostics when the adapter supplied one.</summary>
public static class AdapterWarningDiagnostics
{
    public static Diagnostic Create(string fallbackCode, string warning, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackCode);
        ArgumentNullException.ThrowIfNull(warning);
        return VisualDiagnostic.TryParseWarning(warning, out var code, out var message)
            ? new Diagnostic(code, message, severity)
            : new Diagnostic(fallbackCode, warning, severity);
    }
}
