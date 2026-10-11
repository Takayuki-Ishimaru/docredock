using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocRedock.LicenseAudit;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record Allowlist(string[] AllowedLicenses, List<AllowedPackage> Packages, List<SourceData>? SourceData = null);
    private sealed record SourceData(string Id, string Version, string License, string Source, string? Sha256 = null);
    private sealed record AllowedPackage(string Id, string Version, string License, string? Note = null);
    private sealed record PackageRef(string Id, string Version, string LockFile);
    private sealed record Violation(string Code, string Message, string? Package = null);

    private sealed record Component(string Type, string Name, string Version, string Purl, ComponentLicense[] Licenses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExternalReference[]? ExternalReferences = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ComponentHash[]? Hashes = null)
    {
        [JsonPropertyName("bom-ref")]
        public string BomRef => Purl;
    }
    private sealed record ExternalReference(string Type, string Url);
    private sealed record ComponentHash(string Alg, string Content);

    private sealed record ComponentLicense(License License);
    private sealed record License(string Id);

    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(GetOption(args, "root") ?? Directory.GetCurrentDirectory());
        var allowlistPath = Path.GetFullPath(GetOption(args, "allowlist") ?? Path.Combine(root, "licenses", "allowlist.json"));
        var output = GetOption(args, "output");

        try
        {
            if (!File.Exists(allowlistPath))
            {
                Console.Error.WriteLine($"License allowlist not found: {allowlistPath}");
                return 2;
            }
            var allowlist = JsonSerializer.Deserialize<Allowlist>(File.ReadAllText(allowlistPath), JsonOptions)
                ?? throw new InvalidDataException("License allowlist is empty.");
            var packages = ReadPackages(root);
            var violations = Validate(packages, allowlist);
            var components = packages
                .Select(p => allowlist.Packages.FirstOrDefault(a => Same(a.Id, p.Id) && a.Version == p.Version))
                .Where(p => p is not null)
                .Select(p => new Component("library", p!.Id, p.Version, $"pkg:nuget/{p.Id.ToLowerInvariant()}@{p.Version}",
                    [new ComponentLicense(new License(p.License))]))
                .Concat((allowlist.SourceData ?? []).Where(data => data.Id is not null && data.Version is not null).Select(data => new Component("data", data.Id, data.Version,
                    $"pkg:generic/{Uri.EscapeDataString(data.Id)}@{Uri.EscapeDataString(data.Version)}",
                    [new ComponentLicense(new License(data.License))], [new ExternalReference("distribution", data.Source)],
                    data.Sha256 is null ? null : [new ComponentHash("SHA-256", data.Sha256)])))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Version, StringComparer.Ordinal)
                .ToArray();

            if (output is not null)
                WriteSbom(output, components);

            foreach (var violation in violations)
                Console.Error.WriteLine($"{violation.Code}: {violation.Message}");
            Console.WriteLine($"License audit: {packages.Count} package reference(s), {allowlist.SourceData?.Count ?? 0} source data item(s), {violations.Count} violation(s).");
            return violations.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"License audit failed: {ex.Message}");
            return 2;
        }
    }

    private static List<PackageRef> ReadPackages(string root)
    {
        var result = new List<PackageRef>();
        foreach (var lockPath in Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories)
                     .Where(path => !IsExcluded(path)).OrderBy(path => path, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                foreach (var package in framework.Value.EnumerateObject().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var value = package.Value;
                    if (value.TryGetProperty("type", out var type) &&
                        string.Equals(type.GetString(), "Project", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!value.TryGetProperty("resolved", out var resolved) || string.IsNullOrWhiteSpace(resolved.GetString()))
                    {
                        result.Add(new PackageRef(package.Name, "", Path.GetRelativePath(root, lockPath)));
                        continue;
                    }
                    result.Add(new PackageRef(package.Name, resolved.GetString()!, Path.GetRelativePath(root, lockPath)));
                }
            }
        }
        return result.DistinctBy(p => (Id: p.Id.ToLowerInvariant(), p.Version)).ToList();
    }

    private static List<Violation> Validate(IEnumerable<PackageRef> packages, Allowlist allowlist)
    {
        var allowedLicenses = allowlist.AllowedLicenses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = allowlist.Packages.GroupBy(p => (p.Id.ToLowerInvariant(), p.Version))
            .ToDictionary(g => g.Key, g => g.Last());
        var violations = new List<Violation>();
        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(package.Version))
            {
                violations.Add(new("LIC001", $"Package '{package.Id}' has no resolved version ({package.LockFile}).", package.Id));
                continue;
            }
            if (!entries.TryGetValue((package.Id.ToLowerInvariant(), package.Version), out var allowed))
            {
                violations.Add(new("LIC002", $"Package {package.Id} {package.Version} is not in the explicit allowlist.", package.Id));
                continue;
            }
            if (string.IsNullOrWhiteSpace(allowed.License))
                violations.Add(new("LIC003", $"Package {package.Id} {package.Version} has an unknown license.", package.Id));
            else if (!allowedLicenses.Contains(allowed.License))
                violations.Add(new("LIC004", $"Package {package.Id} {package.Version} uses non-permitted license '{allowed.License}'.", package.Id));
        }
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var data in allowlist.SourceData ?? [])
        {
            if (string.IsNullOrWhiteSpace(data.Id) || string.IsNullOrWhiteSpace(data.Version) || !sourceIds.Add(data.Id) ||
                !Uri.TryCreate(data.Source, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                violations.Add(new("LIC005", $"Source data '{data.Id}' requires a unique ID, version and HTTPS source."));
            if (string.IsNullOrWhiteSpace(data.License) || !allowedLicenses.Contains(data.License))
                violations.Add(new("LIC006", $"Source data '{data.Id}' uses unknown or non-permitted license '{data.License}'."));
            if (data.Sha256 is not null && (data.Sha256.Length != 64 || !data.Sha256.All(Uri.IsHexDigit)))
                violations.Add(new("LIC007", $"Source data '{data.Id}' has an invalid SHA-256."));
        }
        return violations;
    }

    private static void WriteSbom(string output, IReadOnlyList<Component> components)
    {
        var path = output.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(output)
            : Path.Combine(Path.GetFullPath(output), "sbom.cdx.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var digestInput = string.Join("\n", components.Select(c => $"{c.Name}|{c.Version}|{c.Licenses[0].License.Id}"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant();
        var serial = $"{digest[..8]}-{digest[8..12]}-{digest[12..16]}-{digest[16..20]}-{digest[20..32]}";
        var bom = new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.5",
            serialNumber = $"urn:uuid:{serial}",
            version = 1,
            components
        };
        var json = JsonSerializer.Serialize(bom, JsonOptions) + "\n";
        File.WriteAllText(path, json, new UTF8Encoding(false));
        Console.WriteLine($"SBOM: {path}");
    }

    private static bool IsExcluded(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(x => x is ".git" or "bin" or "obj");

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == $"--{name}" && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith($"--{name}=", StringComparison.Ordinal)) return args[i][(name.Length + 3)..];
        }
        return null;
    }
}
