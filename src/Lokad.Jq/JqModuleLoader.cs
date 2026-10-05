using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Jq;

// Per-execution module cache and loading stack. Search and reads are hosted;
// no ambient filesystem, home, or executable-origin lookups are performed.
internal sealed class JqLoadedModule
{
    internal JqLoadedModule(
        string canonicalPath,
        JsonObject? metadata,
        IReadOnlyList<JqModuleImport> imports,
        IReadOnlyList<JqFunctionDefinition> definitions,
        JqEnvironment moduleEnv)
    {
        CanonicalPath = canonicalPath;
        Metadata = metadata;
        Imports = imports;
        Definitions = definitions;
        ModuleEnv = moduleEnv;
    }

    internal string CanonicalPath { get; }
    internal JsonObject? Metadata { get; }
    internal IReadOnlyList<JqModuleImport> Imports { get; }
    internal IReadOnlyList<JqFunctionDefinition> Definitions { get; }
    internal JqEnvironment ModuleEnv { get; }
}

internal sealed class JqModuleLoader(
    IJqHost host,
    JqBudget budget,
    JqRuntime runtime,
    JqEnvironment rootEnvironment,
    IReadOnlyList<string> libraryDirs,
    string workingDirectory)
{
    private readonly Dictionary<string, JqLoadedModule> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);

    internal async Task<JqEnvironment> LoadMainImportsAsync(
        IReadOnlyList<JqModuleImport> imports,
        string importerDir,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(importerDir);
        JqEnvironment env = rootEnvironment;
        foreach (JqModuleImport import in imports)
            env = await ApplyImportAsync(env, import, importerDir, 0, cancellationToken).ConfigureAwait(false);
        return env;
    }

    private async Task<JqEnvironment> ApplyImportAsync(
        JqEnvironment env,
        JqModuleImport import,
        string importerDir,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > JqBudget.MaximumDepth)
            throw new JqQuotaException("module nesting limit exceeded");
        string? validation = JqModulePathValidation.Validate(import.RelPath);
        if (validation is not null)
            throw new JqException(validation);
        if (import.IsData)
        {
            JsonNode? data = await TryLoadDataAsync(import, importerDir, cancellationToken).ConfigureAwait(false);
            if (data is null)
                return env;
            budget.ChargeTree(data);
            string alias = import.Alias ?? string.Empty;
            env = env.Extend(alias, data);
            env = env.Extend(alias + "::" + alias, data);
            return env;
        }
        if (import.IsInclude)
        {
            var included = await TryLoadFuncModuleAsync(import, importerDir, depth, cancellationToken).ConfigureAwait(false);
            if (included is null)
                return env;
            var closures = new Dictionary<(string Name, int Arity), JqUserClosure>();
            included.ModuleEnv.CollectFunctionClosures(closures);
            // Deterministic order for reviewable shadowing: later includes win
            // through environment order, independent of dictionary order.
            var ordered = new List<KeyValuePair<(string Name, int Arity), JqUserClosure>>(closures);
            ordered.Sort((left, right) =>
            {
                int name = string.CompareOrdinal(left.Key.Name, right.Key.Name);
                return name != 0 ? name : left.Key.Arity.CompareTo(right.Key.Arity);
            });
            foreach (var entry in ordered)
                env = env.ExtendClosure(entry.Key.Name, entry.Key.Arity, entry.Value);
            return env;
        }
        {
            var module = await TryLoadFuncModuleAsync(import, importerDir, depth, cancellationToken).ConfigureAwait(false);
            if (module is null)
                return env;
            string alias = import.Alias ?? string.Empty;
            // Same-alias imports merge per-symbol with later wins, matching
            // link-time binding where earlier modules supply symbols the later
            // ones do not define.
            if (env.TryGetModule(alias, out JqEnvironment? existing) && existing is not null)
            {
                var merged = new Dictionary<(string Name, int Arity), JqUserClosure>();
                existing.CollectFunctionClosures(merged);
                var incoming = new Dictionary<(string Name, int Arity), JqUserClosure>();
                module.ModuleEnv.CollectFunctionClosures(incoming);
                foreach (var entry in incoming)
                    merged[entry.Key] = entry.Value;
                JqEnvironment mergedEnv = rootEnvironment;
                var ordered = new List<KeyValuePair<(string Name, int Arity), JqUserClosure>>(merged);
                ordered.Sort((left, right) =>
                {
                    int name = string.CompareOrdinal(left.Key.Name, right.Key.Name);
                    return name != 0 ? name : left.Key.Arity.CompareTo(right.Key.Arity);
                });
                foreach (var entry in ordered)
                    mergedEnv = mergedEnv.ExtendClosure(entry.Key.Name, entry.Key.Arity, entry.Value);
                env = env.ExtendModule(alias, mergedEnv);
            }
            else
            {
                env = env.ExtendModule(alias, module.ModuleEnv);
            }
            return env;
        }
    }

    private async Task<JqLoadedModule?> TryLoadFuncModuleAsync(
        JqModuleImport import,
        string importerDir,
        int depth,
        CancellationToken cancellationToken)
    {
        var found = await FindModuleFileAsync(import, importerDir, ".jq", cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            if (import.IsOptional)
                return null;
            throw new JqException("module not found: " + import.RelPath);
        }
        return await LoadFuncModuleAtPathAsync(found.Value.CanonicalPath, found.Value.Content, depth, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> TryLoadDataAsync(
        JqModuleImport import,
        string importerDir,
        CancellationToken cancellationToken)
    {
        var found = await FindModuleFileAsync(import, importerDir, ".json", cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            if (import.IsOptional)
                return null;
            throw new JqException("module not found: " + import.RelPath);
        }
        ReadOnlyMemory<byte> content = found.Value.Content;
        string canonical = found.Value.CanonicalPath;
        budget.ChargeString(Encoding.UTF8.GetCharCount(content.Span));
        if (import.IsRaw)
        {
            string rawText;
            try
            {
                rawText = Helpers.Utf8Text.Decode(content);
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                if (import.IsOptional)
                    return null;
                throw new JqException("error loading data file " + canonical + ": " + ex.Message);
            }
            return JsonValue.Create(rawText);
        }
        try
        {
            var array = new JsonArray();
            int offset = 0;
            while (offset < content.Length)
            {
                if (IsWhitespaceOnly(content.Span[offset..]))
                    break;
                JsonNode? node = runtime.ReadJsonValue(content.Span[offset..], out int consumed);
                offset += consumed;
                array.Add(node?.DeepClone());
            }
            return array;
        }
        catch (JqException ex) when (ex is not JqQuotaException)
        {
            if (import.IsOptional)
                return null;
            throw new JqException("error loading data file " + canonical + ": " + ex.Message);
        }
    }

    private async Task<JqLoadedModule> LoadFuncModuleAtPathAsync(
        string canonicalPath,
        ReadOnlyMemory<byte> content,
        int depth,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(canonicalPath, out JqLoadedModule? cached))
            return cached;
        if (!_loading.Add(canonicalPath))
            throw new JqException("circular import of " + canonicalPath);
        try
        {
            budget.ChargeString(Encoding.UTF8.GetCharCount(content.Span));
            string sourceText;
            try
            {
                sourceText = Helpers.Utf8Text.Decode(content);
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                throw new JqException("error loading module " + canonicalPath + ": " + ex.Message);
            }
            var programSource = new JqProgramSource("file \"" + canonicalPath + "\"", canonicalPath);
            // Pre-scan imports before loading dependencies.
            var preParser = new JqParser(sourceText, programSource, rootEnvironment, budget);
            (JsonObject? metadata, IReadOnlyList<JqModuleImport> imports) = preParser.ParseImportsOnly();
            string childImporterDir = ParentDir(canonicalPath);
            JqEnvironment env = rootEnvironment;
            foreach (JqModuleImport import in imports)
                env = await ApplyImportAsync(env, import, childImporterDir, depth + 1, cancellationToken).ConfigureAwait(false);
            // Full parse with dependencies in scope for include validation.
            var fullParser = new JqParser(sourceText, programSource, env, budget);
            // Seed parser scopes with include-provided functions for validation.
            // The parser seeds from the environment automatically.
            (JsonObject? fullMetadata, IReadOnlyList<JqModuleImport> fullImports, IReadOnlyList<JqFunctionDefinition> definitions) = fullParser.ParseModuleFile();
            foreach (JqFunctionDefinition definition in definitions)
            {
                budget.CheckCancellation();
                env = env.ExtendFunction(definition.Name, definition.Arity, definition);
            }
            var loaded = new JqLoadedModule(canonicalPath, fullMetadata ?? metadata, fullImports, definitions, env);
            _cache[canonicalPath] = loaded;
            return loaded;
        }
        finally
        {
            _loading.Remove(canonicalPath);
        }
    }

    private async Task<(string CanonicalPath, ReadOnlyMemory<byte> Content)?> FindModuleFileAsync(
        JqModuleImport import,
        string importerDir,
        string suffix,
        CancellationToken cancellationToken)
    {
        List<string?> searchDirs = BuildSearchDirs(import, importerDir);
        string rel = import.RelPath;
        string baseName = rel.Contains((char)47, StringComparison.Ordinal) ? rel[(rel.LastIndexOf((char)47) + 1)..] : rel;
        foreach (string? dir in searchDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dir is null)
                break;
            if (dir.Length == 0)
                continue;
            string prefix = dir == "/" ? "/" : dir + "/";
            string[] candidates =
            [
                prefix + rel + suffix,
                prefix + rel + "/jq/main" + suffix,
                prefix + rel + "/" + baseName + suffix,
            ];
            foreach (string candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ChargeBytes(128 + 64L * candidate.Length);
                JqPath path;
                try
                {
                    path = new JqPath(candidate);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                var opened = await JqHostExtensions.GuardHostAsync(() => host.OpenReadAsync(path, cancellationToken)).ConfigureAwait(false);
                if (opened.Error is not null || opened.FileDescriptor is null)
                    continue;
                Exception? failure = null;
                try
                {
                    var result = await host.TryReadAllBytesAsync(
                        opened.FileDescriptor.Value, budget.RemainingInput, budget.ChargeInput, cancellationToken).ConfigureAwait(false);
                    if (result is BoundedReadResult.Complete complete)
                        return (path.Path, complete.Content);
                    if (result is BoundedReadResult.TooLarge)
                        throw new JqQuotaException(budget.InputLimitMessage);
                    continue;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }
                finally
                {
                    await host.CloseOwnedDescriptorAsync(opened.FileDescriptor.Value, failure).ConfigureAwait(false);
                }
            }
        }
        return null;
    }

    private List<string?> BuildSearchDirs(JqModuleImport import, string importerDir)
    {
        var dirs = new List<string?>();
        List<JsonNode?> searchNodes = [];
        if (import.Metadata is not null && import.Metadata.TryGetPropertyValue("search", out JsonNode? searchValue))
        {
            if (searchValue is null)
                searchNodes.Add(null);
            else if (searchValue is JsonValue scalar && scalar.TryGetValue<string>(out string? single))
                searchNodes.Add(searchValue);
            else if (searchValue is JsonArray array)
            {
                foreach (JsonNode? item in array)
                    searchNodes.Add(item);
            }
            else
                searchNodes.Add(searchValue);
        }
        else
        {
            searchNodes.Add(JsonValue.Create("."));
        }
        foreach (JsonNode? node in searchNodes)
        {
            if (node is null)
            {
                dirs.Add(null);
                break;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
            {
                if (text.Length == 0)
                    continue;
                string? resolved = ResolveSearchDir(text, importerDir);
                if (resolved is not null)
                    dirs.Add(resolved);
                continue;
            }
            continue;
        }
        // Top-level -L directories follow per-import search entries.
        foreach (string lib in libraryDirs)
            dirs.Add(lib);
        return dirs;
    }

    private static string? ResolveSearchDir(string entry, string importerDir)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(importerDir);
        if (entry == ".")
            return importerDir;
        if (entry.StartsWith("~/", StringComparison.Ordinal) || entry == "~")
            return null;
        if (entry.StartsWith("$ORIGIN/", StringComparison.Ordinal) || entry == "$ORIGIN")
            return null;
        string combined = entry.StartsWith((char)47)
            ? entry
            : importerDir == "/" ? "/" + entry : importerDir + "/" + entry;
        try
        {
            var raw = JqRawPath.Parse(combined);
            var absolute = raw.ToAbsolute(JqPath.Root);
            return absolute.Path;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string ParentDir(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        int slash = canonicalPath.LastIndexOf((char)47);
        if (slash <= 0)
            return "/";
        return canonicalPath[..slash];
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b != (byte)32 && b != (byte)9 && b != (byte)10 && b != (byte)13)
                return false;
        }
        return true;
    }

    internal async Task<JsonObject> GetModuleMetadataAsync(string relPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        string? validation = JqModulePathValidation.Validate(relPath);
        if (validation is not null)
            throw new JqException(validation);
        var probe = new JqModuleImport(relPath, null, false, null, default, default);
        var found = await FindModuleFileAsync(probe, workingDirectory, ".jq", cancellationToken).ConfigureAwait(false);
        if (found is null)
            throw new JqException("module not found: " + relPath);
        var loaded = await LoadFuncModuleAtPathAsync(found.Value.CanonicalPath, found.Value.Content, 0, cancellationToken).ConfigureAwait(false);
        return BuildMetadataObject(loaded);
    }

    internal JsonObject GetModuleMetadataSync(string relPath)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        // Filters evaluate synchronously while module reads are host-async.
        // In-memory hosts complete synchronously; blocking here keeps the
        // evaluator shape while still mediating all bytes via IJqHost.
        return GetModuleMetadataAsync(relPath, budget.CancellationToken).GetAwaiter().GetResult();
    }

    private JsonObject BuildMetadataObject(JqLoadedModule loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        JsonObject baseObject = loaded.Metadata is not null
            ? (JsonObject)loaded.Metadata.DeepClone()
            : new JsonObject();
        var deps = new JsonArray();
        foreach (JqModuleImport import in loaded.Imports)
        {
            budget.CheckCancellation();
            var dep = new JsonObject();
            if (import.Metadata is not null)
            {
                foreach (var kv in import.Metadata)
                    dep[kv.Key] = kv.Value?.DeepClone();
            }
            dep["relpath"] = JsonValue.Create(import.RelPath);
            if (import.Alias is not null)
                dep["as"] = JsonValue.Create(import.Alias);
            dep["is_data"] = JsonValue.Create(import.IsData);
            budget.ChargeNode();
            budget.ChargeString(import.RelPath.Length + (import.Alias?.Length ?? 0) + 16);
            deps.Add(dep);
        }
        var defs = new JsonArray();
        foreach (JqFunctionDefinition definition in loaded.Definitions)
        {
            budget.CheckCancellation();
            string entry = definition.Name + "/" + definition.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            budget.ChargeNode();
            budget.ChargeString(entry.Length);
            defs.Add(JsonValue.Create(entry));
        }
        baseObject["deps"] = deps;
        baseObject["defs"] = defs;
        budget.ChargeTree(baseObject);
        return baseObject;
    }
}
