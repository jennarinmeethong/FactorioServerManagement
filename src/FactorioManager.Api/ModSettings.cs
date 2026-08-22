using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactorioManager.Api;

/// <summary>Typed discovery, validation, compatibility and atomic persistence for mod settings.</summary>
public sealed class ModSettingsService(DataPaths paths, StateStore store)
{
    public const int CurrentSchemaVersion = 1;
    public string ArtifactPath => Path.Combine(paths.Config, "mod-settings.json");
    public string FactorioInputPath => Path.Combine(paths.Config, "factorio-mod-settings.dat");
    private static JsonSerializerOptions JsonOptions => new(JsonSerializerDefaults.Web);

    public static void ValidateUpdateJson(JsonElement root)
    {
        RejectUnknown(root, "request", "artifact", "confirmStopped");
        var artifact = root.GetProperty("artifact"); RejectUnknown(artifact, "artifact", "schemaVersion", "generatedAtUtc", "compatibility", "values");
        var compatibility = artifact.GetProperty("compatibility"); RejectUnknown(compatibility, "compatibility", "factorioVersion", "expansion", "saveName", "saveSha256", "enabledMods", "catalogFingerprint");
        foreach (var mod in compatibility.GetProperty("enabledMods").EnumerateArray()) RejectUnknown(mod, "enabled mod", "name", "version", "sha256");
        foreach (var value in artifact.GetProperty("values").EnumerateObject())
        {
            ValidateValueWrapper(value.Value, value.Name);
        }
    }

    public async Task<ModSettingsDocument> DiscoverAsync(ServerSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ActiveVersion)) throw new InvalidOperationException("Choose a Factorio version before discovering mod settings.");
        var files = new List<(string Mod, string File)>();
        var root = Path.Combine(paths.Versions, settings.ActiveVersion, "data");
        AddIfPresent(files, "base", Path.Combine(root, "base", "prototypes", "settings.lua"));
        if (settings.Expansion.Equals("space-age", StringComparison.OrdinalIgnoreCase))
            AddIfPresent(files, "space-age", Path.Combine(root, "space-age", "prototypes", "settings.lua"));
        var mods = await store.GetAsync<ModEntry[]>("mods", ct) ?? [];
        foreach (var mod in mods.Where(m => m.Enabled))
        {
            var archive = mod.ArchiveFileName is null ? null : Path.Combine(paths.Mods, mod.ArchiveFileName);
            if (archive is not null && File.Exists(archive))
            {
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("settings.lua", StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    var temp = Path.Combine(Path.GetTempPath(), $"factorio-settings-{Guid.NewGuid():N}.lua");
                    await using (var input = entry.Open()) await using (var output = File.Create(temp)) await input.CopyToAsync(output, ct);
                    files.Add((mod.Name, temp));
                }
            }
            AddIfPresent(files, mod.Name, Path.Combine(paths.Mods, mod.Name, "settings.lua"));
        }
        var diagnostics = new List<string>();
        var definitions = files.SelectMany(x => Parse(x.Mod, File.ReadAllText(x.File), diagnostics)).GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", definitions.Select(DefinitionFingerprint)))));
        var compatibility = await ComputeCompatibilityAsync(settings, mods, fingerprint, ct);
        var values = definitions.ToDictionary(x => x.Id, x => ToValue(x.Default, x.ValueType), StringComparer.Ordinal);
        return new(new ModSettingsArtifact(CurrentSchemaVersion, DateTimeOffset.UtcNow, compatibility, values), definitions, diagnostics.ToArray());
    }

    public async Task<ModSettingsCompatibility> ComputeCompatibilityAsync(ServerSettings settings, ModEntry[]? mods = null, string? fingerprint = null, CancellationToken ct = default)
    {
        mods ??= await store.GetAsync<ModEntry[]>("mods", ct) ?? [];
        var saveName = settings.ActiveSave is null ? null : Path.GetFileName(settings.ActiveSave);
        var save = saveName is null ? null : Path.Combine(paths.Saves, saveName);
        var hash = save is not null && File.Exists(save) ? Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(save), ct)) : null;
        return new(settings.ActiveVersion ?? "", settings.Expansion, saveName, hash, mods.Where(x => x.Enabled).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new EnabledModCompatibility(x.Name, x.Version, x.Sha256)).ToArray(), fingerprint ?? "");
    }

    public async Task<ModSettingsDocument?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ArtifactPath)) return null;
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ArtifactPath, ct));
            RejectUnknown(json.RootElement, "document", "artifact", "definitions", "diagnostics");
            var artifact = json.RootElement.GetProperty("artifact"); RejectUnknown(artifact, "artifact", "schemaVersion", "generatedAtUtc", "compatibility", "values");
            if (!artifact.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version) || version != CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported mod-settings schema version.");
            var compatibility = artifact.GetProperty("compatibility"); RejectUnknown(compatibility, "compatibility", "factorioVersion", "expansion", "saveName", "saveSha256", "enabledMods", "catalogFingerprint");
            foreach (var mod in compatibility.GetProperty("enabledMods").EnumerateArray()) RejectUnknown(mod, "enabled mod", "name", "version", "sha256");
            foreach (var property in artifact.GetProperty("values").EnumerateObject())
            {
                ValidateValueWrapper(property.Value, property.Name);
            }
            foreach (var definition in json.RootElement.GetProperty("definitions").EnumerateArray()) RejectUnknown(definition, "definition", "id", "modName", "settingType", "valueType", "default", "minimum", "maximum", "allowedValues", "required");
            foreach (var diagnostic in json.RootElement.GetProperty("diagnostics").EnumerateArray()) if (diagnostic.ValueKind != JsonValueKind.String) throw new InvalidOperationException("The mod-settings diagnostics must be strings.");
            var document = JsonSerializer.Deserialize<ModSettingsDocument>(json.RootElement.GetRawText(), JsonOptions) ?? throw new InvalidOperationException("The mod-settings artifact is empty.");
            Validate(document);
            return document;
        }
        catch (JsonException e) { throw new InvalidOperationException("The mod-settings artifact is invalid JSON.", e); }
    }

    public async Task SaveAsync(ModSettingsDocument document, ServerSettings settings, CancellationToken ct = default)
    {
        if (document.Artifact.SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("Unsupported mod-settings schema version.");
        Validate(document);
        var discovered = await DiscoverAsync(settings, ct);
        if (!CompatibilityEquals(document.Artifact.Compatibility, discovered.Artifact.Compatibility)) throw new InvalidOperationException("The mod-settings compatibility snapshot is stale. Refresh the catalog and try again.");
        var json = JsonSerializer.Serialize(document with { Artifact = document.Artifact with { GeneratedAtUtc = DateTimeOffset.UtcNow } }, JsonOptions);
        var temp = ArtifactPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = ArtifactPath + ".bak";
        await File.WriteAllTextAsync(temp, json, ct);
        try
        {
            if (File.Exists(ArtifactPath))
            {
                try { File.Replace(temp, ArtifactPath, backup, true); }
                catch (PlatformNotSupportedException) { File.Copy(ArtifactPath, backup, true); File.Move(temp, ArtifactPath, true); }
            }
            else File.Move(temp, ArtifactPath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Validate(ModSettingsDocument document)
    {
        if (document.Artifact.SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("Unsupported mod-settings schema version.");
        if (document.Definitions is null) throw new InvalidOperationException("The mod-settings definitions are missing.");
        var definitions = new Dictionary<string, ModSettingDefinition>(StringComparer.Ordinal);
        foreach (var definition in document.Definitions)
        {
            ValidateDefinition(definition);
            if (!definitions.TryAdd(definition.Id, definition)) throw new InvalidOperationException($"The mod-settings artifact contains duplicate setting id '{definition.Id}'.");
        }
        if (document.Artifact.Values.Keys.Any(x => !definitions.ContainsKey(x))) throw new InvalidOperationException("The mod-settings artifact contains an unknown setting.");
        foreach (var (id, value) in document.Artifact.Values)
        {
            var definition = definitions[id];
            if (!string.Equals(value.Type, definition.ValueType, StringComparison.Ordinal)) throw new InvalidOperationException($"Setting '{id}' has the wrong primitive type.");
            var raw = value.Value;
            if (raw is null) throw new InvalidOperationException($"Setting '{id}' must have a non-null value.");
            if (raw is JsonElement element) { if (!PrimitiveMatches(element, definition.ValueType)) throw new InvalidOperationException($"Setting '{id}' has an invalid primitive value."); raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False ? element.GetBoolean() : element.ValueKind == JsonValueKind.Number ? element.GetDouble() : element; }
            else if (!TypedValueMatches(raw, definition.ValueType)) throw new InvalidOperationException($"Setting '{id}' has an invalid primitive value.");
            if (definition.ValueType == "color") ValidateColor(raw, id);
            if (definition.AllowedValues is { Length: > 0 } && !definition.AllowedValues.Contains(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)) throw new InvalidOperationException($"Setting '{id}' has a disallowed value.");
            if (raw is IConvertible convertible && double.TryParse(convertible.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && ((definition.Minimum is not null && number < definition.Minimum) || (definition.Maximum is not null && number > definition.Maximum))) throw new InvalidOperationException($"Setting '{id}' is outside its allowed range.");
        }
        foreach (var required in document.Definitions.Where(x => x.Required)) if (!document.Artifact.Values.ContainsKey(required.Id)) throw new InvalidOperationException($"Required setting '{required.Id}' is missing.");
    }

    private static void ValidateDefinition(ModSettingDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.ModName) || string.IsNullOrWhiteSpace(definition.SettingType) || string.IsNullOrWhiteSpace(definition.ValueType))
            throw new InvalidOperationException("Every mod-setting definition must have non-empty id, mod name, setting type, and value type.");
        if (definition.SettingType is not ("startup" or "runtime-global" or "runtime-per-user"))
            throw new InvalidOperationException($"Setting '{definition.Id}' has an unsupported setting type.");
        if (definition.ValueType is not ("bool" or "int" or "double" or "string" or "enum" or "color"))
            throw new InvalidOperationException($"Setting '{definition.Id}' has an unsupported value type.");
        if (definition.Default is null || !TypedValueMatchesOrJson(definition.Default, definition.ValueType))
            throw new InvalidOperationException($"Setting '{definition.Id}' has an invalid default value.");
        if (definition.Minimum is not null && !double.IsFinite(definition.Minimum.Value) || definition.Maximum is not null && !double.IsFinite(definition.Maximum.Value))
            throw new InvalidOperationException($"Setting '{definition.Id}' has a non-finite range.");
        if (definition.Minimum is not null && definition.Maximum is not null && definition.Minimum > definition.Maximum)
            throw new InvalidOperationException($"Setting '{definition.Id}' has a minimum greater than its maximum.");
        if (definition.AllowedValues is { Length: 0 }) throw new InvalidOperationException($"Setting '{definition.Id}' has an empty allowed-values list.");
        if (definition.AllowedValues is not null)
        {
            if (definition.ValueType is not ("string" or "enum" or "int" or "double") || definition.AllowedValues.Any(string.IsNullOrEmpty) || definition.AllowedValues.Distinct(StringComparer.Ordinal).Count() != definition.AllowedValues.Length)
                throw new InvalidOperationException($"Setting '{definition.Id}' has allowed values inconsistent with its value type.");
            if (definition.ValueType is "int" or "double" && definition.AllowedValues.Any(value => !TryParseNumber(value, definition.ValueType, out _)))
                throw new InvalidOperationException($"Setting '{definition.Id}' has allowed values inconsistent with its value type.");
            if (definition.ValueType == "enum" && !definition.AllowedValues.Contains(Convert.ToString(definition.Default, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal))
                throw new InvalidOperationException($"Setting '{definition.Id}' has a default outside its allowed values.");
        }
        if (definition.ValueType == "enum" && definition.AllowedValues is not { Length: > 0 })
            throw new InvalidOperationException($"Enum setting '{definition.Id}' must have non-empty allowed values.");
    }

    private static bool TypedValueMatchesOrJson(object value, string type)
    {
        if (value is ModSettingValue wrapper)
            return string.Equals(wrapper.Type, type, StringComparison.Ordinal) && wrapper.Value is not null && TypedValueMatchesOrJson(wrapper.Value, type);
        if (value is JsonElement element) return element.ValueKind != JsonValueKind.Null && PrimitiveMatches(element, type);
        return TypedValueMatches(value, type);
    }

    public async Task MaterializeFactorioInputAsync(ModSettingsDocument document, CancellationToken ct = default)
    {
        Validate(document);
        var groups = document.ValuesByType();
        var version = ParseVersion(document.Artifact.Compatibility.FactorioVersion);
        var temp = FactorioInputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            foreach (var part in version) writer.Write(part);
            writer.Write(false);
            WriteDictionary(writer, new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["startup"] = groups.GetValueOrDefault("startup", new(StringComparer.Ordinal)),
                ["runtime-global"] = groups.GetValueOrDefault("runtime-global", new(StringComparer.Ordinal)),
                ["runtime-per-user"] = groups.GetValueOrDefault("runtime-per-user", new(StringComparer.Ordinal))
            });
            await stream.FlushAsync(ct);
        }
        try { File.Move(temp, FactorioInputPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static ushort[] ParseVersion(string value) { var result = new ushort[4]; var parts = value.Split('.', StringSplitOptions.TrimEntries); for (var i = 0; i < Math.Min(parts.Length, 4); i++) result[i] = ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (ushort)0; return result; }
    private static void WriteDictionary(BinaryWriter writer, IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, object?>>> dictionary) { writer.Write((byte)5); WriteUInt(writer, (uint)dictionary.Count); foreach (var pair in dictionary.OrderBy(x => x.Key, StringComparer.Ordinal)) { WriteString(writer, pair.Key); WriteSettingDictionary(writer, pair.Value); } }
    private static void WriteSettingDictionary(BinaryWriter writer, IReadOnlyDictionary<string, Dictionary<string, object?>> values) { writer.Write((byte)5); WriteUInt(writer, (uint)values.Count); foreach (var pair in values.OrderBy(x => x.Key, StringComparer.Ordinal)) { WriteString(writer, pair.Key); WriteValueDictionary(writer, pair.Value); } }
    private static void WriteValueDictionary(BinaryWriter writer, IReadOnlyDictionary<string, object?> values) { writer.Write((byte)5); WriteUInt(writer, (uint)values.Count); foreach (var pair in values.OrderBy(x => x.Key, StringComparer.Ordinal)) { WriteString(writer, pair.Key); WriteValue(writer, pair.Value); } }
    private static void WriteValue(BinaryWriter writer, object? value) { if (value is JsonElement element) { value = element.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number when element.TryGetInt64(out var i) => i, JsonValueKind.Number => element.GetDouble(), JsonValueKind.String => element.GetString(), JsonValueKind.Object when element.TryGetProperty("r", out var r) => new ModSettingColor(r.GetDouble(), element.GetProperty("g").GetDouble(), element.GetProperty("b").GetDouble(), element.TryGetProperty("a", out var a) ? a.GetDouble() : 1), JsonValueKind.Null => null, _ => throw new InvalidOperationException("Unsupported JSON mod-setting value.") }; } switch (value) { case ModSettingColor color: WriteValueDictionary(writer, new Dictionary<string, object?> { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B, ["a"] = color.A }); break; case null: writer.Write((byte)0); break; case bool b: writer.Write((byte)1); writer.Write(b); break; case double d: writer.Write((byte)2); writer.Write(d); break; case float f: writer.Write((byte)2); writer.Write((double)f); break; case int n: writer.Write((byte)6); WriteSigned(writer, n); break; case long n: writer.Write((byte)6); WriteSigned(writer, checked((int)n)); break; case string s: writer.Write((byte)3); WriteString(writer, s); break; default: throw new InvalidOperationException("Unsupported mod-setting value for Factorio PropertyTree."); } }
    private static void WriteString(BinaryWriter writer, string? value) { writer.Write(value is null or ""); if (string.IsNullOrEmpty(value)) return; var bytes = Encoding.UTF8.GetBytes(value); WriteUInt(writer, (uint)bytes.Length); writer.Write(bytes); }
    private static void WriteUInt(BinaryWriter writer, ulong value) { while (value >= 0x80) { writer.Write((byte)(value | 0x80)); value >>= 7; } writer.Write((byte)value); }
    private static void WriteSigned(BinaryWriter writer, long value) => WriteUInt(writer, (ulong)((value << 1) ^ (value >> 63)));

    private static void AddIfPresent(List<(string Mod, string File)> files, string mod, string file) { if (File.Exists(file)) files.Add((mod, file)); }
    private static string DefinitionFingerprint(ModSettingDefinition x) => JsonSerializer.Serialize(x, JsonOptions);
    private static ModSettingValue ToValue(object? value, string type) => type switch { "bool" => ModSettingValue.Boolean(value is true), "int" => ModSettingValue.Integer(value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture)), "double" => ModSettingValue.Double(value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture)), "color" when value is ModSettingColor color => new("color", color), "color" => throw new InvalidOperationException("Color setting defaults must be structured."), "enum" => ModSettingValue.Enum(value?.ToString() ?? ""), _ => ModSettingValue.String(value?.ToString() ?? "") };
    private static bool CompatibilityEquals(ModSettingsCompatibility a, ModSettingsCompatibility b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions);
    private static bool PrimitiveMatches(JsonElement value, string type) => type switch { "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "double" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d), "string" or "enum" => value.ValueKind == JsonValueKind.String, "color" => IsValidColor(value), _ => false };
    private static bool TypedValueMatches(object value, string type) => type switch { "bool" => value is bool, "int" => value is sbyte or byte or short or ushort or int or uint or long, "double" => value is float or double or decimal, "string" or "enum" => value is string, "color" => value is ModSettingColor color && new[] { color.R, color.G, color.B, color.A }.All(IsColorComponent), _ => false };
    private static void ValidateValueWrapper(JsonElement settingValue, string id)
    {
        RejectUnknown(settingValue, $"value {id}", "type", "value");
        if (!settingValue.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"Setting '{id}' must have a string type.");
        if (!settingValue.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null) throw new InvalidOperationException($"Setting '{id}' must have a non-null value.");
        if (!PrimitiveMatches(value, type.GetString() ?? "")) throw new InvalidOperationException($"Setting '{id}' has an invalid primitive value.");
    }
    private static void ValidateColor(object? raw, string id)
    {
        var valid = raw switch { ModSettingColor color => new[] { color.R, color.G, color.B, color.A }.All(IsColorComponent), JsonElement element => IsValidColor(element), _ => false };
        if (!valid) throw new InvalidOperationException($"Setting '{id}' has an invalid color value.");
    }
    private static bool IsValidColor(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(x => x.Name is not ("r" or "g" or "b" or "a"))) return false;
        return TryColorComponent(value, "r", out var r) && TryColorComponent(value, "g", out var g) && TryColorComponent(value, "b", out var b) && TryColorComponent(value, "a", out var a) && new[] { r, g, b, a }.All(IsColorComponent);
    }
    private static bool IsColorComponent(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool TryColorComponent(JsonElement value, string name, out double component)
    {
        component = 0;
        return value.TryGetProperty(name, out var property) && property.TryGetDouble(out component);
    }
    private static void RejectUnknown(JsonElement value, string label, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"The mod-settings {label} must be an object.");
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = value.EnumerateObject().FirstOrDefault(x => !set.Contains(x.Name));
        if (value.EnumerateObject().Any(x => !set.Contains(x.Name))) throw new InvalidOperationException($"The mod-settings artifact contains unsupported key '{unknown.Name}'.");
    }
    private static IEnumerable<ModSettingDefinition> Parse(string mod, string source, List<string> diagnostics)
    {
        var block = new Regex(@"data:extend\s*\(\s*\{(?<body>.*)\}\s*\)", RegexOptions.Singleline).Match(source).Groups["body"].Value;
        foreach (Match item in Regex.Matches(block, @"\{(?:(?!\n\s*\}).)*?\}", RegexOptions.Singleline))
        {
            var text = item.Value; var name = MatchValue(text, "name"); var type = MatchValue(text, "type");
            if (string.IsNullOrWhiteSpace(name) || !type.StartsWith("string-setting", StringComparison.Ordinal) && !type.EndsWith("-setting", StringComparison.Ordinal)) { if (!string.IsNullOrWhiteSpace(name)) diagnostics.Add($"Unsupported setting declaration in {mod}."); continue; }
            var valueType = type switch { "bool-setting" => "bool", "int-setting" => "int", "double-setting" => "double", "string-setting" => "string", "color-setting" => "color", _ => "" };
            if (valueType == "") { diagnostics.Add($"Unsupported setting type in {mod}."); continue; }
            var settingType = MatchValue(text, "setting_type"); if (settingType is not ("startup" or "runtime-global" or "runtime-per-user")) { diagnostics.Add($"Unsupported setting scope for {name}."); continue; }
            var allowedMatch = Regex.Match(text, @"allowed_values\s*=\s*\{(?<v>[^}]*)\}", RegexOptions.Singleline);
            var allowedBefore = diagnostics.Count;
            var allowed = allowedMatch.Success ? ParseAllowedValues(allowedMatch.Groups["v"].Value, valueType, diagnostics, name, mod) : null;
            if (Regex.IsMatch(text, @"allowed_values\s*=") && !allowedMatch.Success) { diagnostics.Add($"Malformed allowed_values declaration for {name} in {mod}."); continue; }
            if (diagnostics.Count != allowedBefore) continue;
            if (valueType == "string" && allowed is { Length: > 0 }) valueType = "enum";
            if (!Regex.IsMatch(text, @"\bdefault_value\s*=")) { diagnostics.Add($"Missing default_value for setting {name} in {mod}; definition omitted."); continue; }
            var before = diagnostics.Count;
            var parsedDefault = MatchPrimitive(text, "default_value", valueType, diagnostics, name, mod);
            if (diagnostics.Count != before) continue;
            var minimum = Number(text, "minimum_value", valueType, diagnostics, name, mod);
            var maximum = Number(text, "maximum_value", valueType, diagnostics, name, mod);
            if (diagnostics.Count != before) continue;
            yield return new(name, mod, settingType, valueType, parsedDefault, minimum, maximum, allowed, false);
        }
    }
    private static string MatchValue(string text, string key) => Regex.Match(text, $"\\b{key}\\s*=\\s*\\\"(?<v>[^\\\"]+)").Groups["v"].Value;
    private static double? Number(string text, string key, string type, List<string> diagnostics, string name, string mod)
    {
        var declaration = Regex.Match(text, $@"\b{key}\s*=\s*(?<v>[^,}}]+)");
        if (!declaration.Success) return null;
        var token = declaration.Groups["v"].Value.Trim();
        if (TryParseNumber(token, type, out var number)) return number;
        diagnostics.Add($"Malformed {key} for {name} in {mod}.");
        return null;
    }
    private static string[]? ParseAllowedValues(string body, string type, List<string> diagnostics, string name, string mod)
    {
        var tokens = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return [];
        var values = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (type is "int" or "double")
            {
                if (!TryParseNumber(token, type, out var number))
                {
                    diagnostics.Add($"Malformed allowed_values for {name} in {mod}.");
                    return null;
                }
                values.Add(number.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var match = Regex.Match(token, @"^""(?<s>[^""]*)""$");
                if (!match.Success)
                {
                    diagnostics.Add($"Malformed allowed_values for {name} in {mod}.");
                    return null;
                }
                values.Add(match.Groups["s"].Value);
            }
        }
        return values.Distinct(StringComparer.Ordinal).ToArray();
    }
    private static bool TryParseNumber(string token, string type, out double number)
    {
        if (type == "int" && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            number = integer;
            return true;
        }
        if (type == "double" && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)) return true;
        number = 0;
        return false;
    }
    private static object? MatchPrimitive(string text, string key, string type, List<string> diagnostics, string name, string mod)
    {
        var color = Regex.Match(text, $@"\b{key}\s*=\s*\{{(?<v>[^}}]*)\}}");
        if (type == "color" && color.Success)
        {
            var channels = Regex.Matches(color.Groups["v"].Value, @"(?<k>[rgba])\s*=\s*(?<v>[-0-9.]+)").ToDictionary(x => x.Groups["k"].Value, x => x.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
            if (channels.TryGetValue("r", out var r) && channels.TryGetValue("g", out var g) && channels.TryGetValue("b", out var b) && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var rv) && double.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out var gv) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bv))
            {
                var av = channels.TryGetValue("a", out var a) && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedA) ? parsedA : 1d;
                if (new[] { rv, gv, bv, av }.All(x => x is >= 0 and <= 1)) return new ModSettingColor(rv, gv, bv, av);
            }
            diagnostics.Add($"Malformed or out-of-range color default for {name} in {mod}."); return null;
        }
        var pattern = type switch
        {
            "bool" => $@"\b{key}\s*=\s*(?<v>true|false)\b",
            "int" => $@"\b{key}\s*=\s*(?<v>[^,}}]+)",
            "double" => $@"\b{key}\s*=\s*(?<v>[^,}}]+)",
            "string" or "enum" => $"\\b{key}\\s*=\\s*\\\"(?<v>[^\\\"]*)\\\"",
            _ => $@"\b{key}\s*=\s*(?<v>[^,}}]+)"
        };
        var m = Regex.Match(text, pattern);
        if (!m.Success) { diagnostics.Add($"Malformed {key} for {name} in {mod}."); return null; }
        var v = m.Groups["v"].Value;
        try { return type switch { "bool" => bool.Parse(v), "int" => int.Parse(v, CultureInfo.InvariantCulture), "double" => double.Parse(v, CultureInfo.InvariantCulture), _ => v }; } catch { diagnostics.Add($"Malformed {key} for {name} in {mod}."); return null; }
    }
}

internal static class ModSettingsDocumentExtensions
{
    public static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> ValuesByType(this ModSettingsDocument document) => document.Definitions.GroupJoin(document.Artifact.Values, d => d.Id, v => v.Key, (d, values) => new { d, value = values.Select(x => x.Value.Value).FirstOrDefault() }).Where(x => x.value is not null).GroupBy(x => x.d.SettingType, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.d.Id, x => new Dictionary<string, object?> { ["value"] = x.value }), StringComparer.Ordinal);
}
