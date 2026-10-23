using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Jq;

// Executable imports retain linked bindings; metadata inspection reads fresh
// library syntax. All search and byte I/O goes through the host.
internal sealed class JqModuleLoader(
    IJqHost host,
    JqBudget budget,
    JqRuntime runtime,
    JqEnvironment rootEnvironment,
    IReadOnlyList<string> libraryDirs,
    string workingDirectory)
{
    private sealed record LoadedModule(JqEnvironment ModuleEnv,
        IReadOnlyDictionary<(string Name, int Arity), JqUserClosure> Exports);

    private readonly Dictionary<string, LoadedModule> _cache = new(StringComparer.Ordinal);
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
            env = await ApplyImportAsync(env, import, importerDir, 1, cancellationToken).ConfigureAwait(false);
        return env;
    }

    private async Task<JqEnvironment> ApplyImportAsync(
        JqEnvironment env,
        JqModuleImport import,
        string importerDir,
        int depth,
        CancellationToken cancellationToken)
    {
        JqRecursion.CheckDepth(depth, "module nesting limit exceeded");
        string? validation = JqModulePathValidation.Validate(import.RelPath, budget);
        if (validation is not null)
            throw new JqException(validation);
        if (import.IsData)
        {
            JsonNode? data = await TryLoadDataAsync(import, importerDir, cancellationToken).ConfigureAwait(false);
            if (data is null)
                return env;
            string alias = import.Alias ?? string.Empty;
            budget.ReserveMetadata(256, 2);
            env = env.Extend(alias, data);
            env = env.Extend(alias + "::" + alias, data);
            return env;
        }
        if (import.IsInclude)
        {
            var included = await TryLoadFuncModuleAsync(import, importerDir, depth, cancellationToken).ConfigureAwait(false);
            if (included is null)
                return env;
            foreach (var entry in included.Exports)
            {
                budget.ReserveMetadata(128, 1);
                env = env.ExtendClosure(entry.Key.Name, entry.Key.Arity, entry.Value);
            }
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
                using var scratch = new JqStorage(budget.MemoryBudget);
                var merged = new Dictionary<(string Name, int Arity), JqUserClosure>();
                using (budget.UseStorage(scratch))
                {
                    existing.CollectFunctionClosures(merged, budget);
                    foreach (var entry in module.Exports)
                    {
                        budget.CheckCancellation();
                        if (!merged.ContainsKey(entry.Key)) budget.ReserveMetadata(256, 1);
                        merged[entry.Key] = entry.Value;
                    }
                }
                JqEnvironment mergedEnv = rootEnvironment;
                foreach (var entry in merged)
                {
                    budget.ReserveMetadata(128, 1);
                    mergedEnv = mergedEnv.ExtendClosure(entry.Key.Name, entry.Key.Arity, entry.Value);
                }
                budget.ReserveMetadata(128, 1);
                env = env.ExtendModule(alias, mergedEnv);
            }
            else
            {
                budget.ReserveMetadata(128, 1);
                env = env.ExtendModule(alias, module.ModuleEnv);
            }
            return env;
        }
    }

    private async Task<LoadedModule?> TryLoadFuncModuleAsync(
        JqModuleImport import,
        string importerDir,
        int depth,
        CancellationToken cancellationToken)
    {
        foreach (JqPath path in ModulePaths(import, importerDir, ".jq", cancellationToken))
        {
            if (_cache.TryGetValue(path.Path, out LoadedModule? cached)) return cached;
            var result = await TryReadModuleFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            if (result is JqReadResult.Failed)
            {
                if (import.IsOptional) return null;
                throw new JqException("error loading module " + path.Path + ": input read failed");
            }
            using var content = (JqReadResult.Complete)result;
            return await LoadFuncModuleAtPathAsync(path.Path, content.Content, depth, cancellationToken).ConfigureAwait(false);
        }
        if (import.IsOptional) return null;
        throw new JqException("module not found: " + import.RelPath);
    }

    private async Task<JsonNode?> TryLoadDataAsync(
        JqModuleImport import,
        string importerDir,
        CancellationToken cancellationToken)
    {
        JqReadResult.Complete? loaded = null;
        string canonical = string.Empty;
        foreach (JqPath path in ModulePaths(import, importerDir, ".json", cancellationToken))
        {
            var result = await TryReadModuleFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            if (result is JqReadResult.Failed)
            {
                if (import.IsOptional) return null;
                throw new JqException("error loading data file " + path.Path + ": input read failed");
            }
            loaded = (JqReadResult.Complete)result;
            canonical = path.Path;
            break;
        }
        if (loaded is null)
        {
            if (import.IsOptional) return null;
            throw new JqException("module not found: " + import.RelPath);
        }
        using var ownedContent = loaded;
        ReadOnlyMemory<byte> content = ownedContent.Content;
        if (import.IsRaw)
        {
            budget.ReserveString(Encoding.UTF8.GetCharCount(content.Span));
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
            return runtime.ReadJsonArray(content.Span, 1);
        }
        catch (JqException ex) when (ex is not JqQuotaException)
        {
            if (import.IsOptional)
                return null;
            throw new JqException("error loading data file " + canonical + ": " + ex.Message);
        }
    }

    private async Task<LoadedModule> LoadFuncModuleAtPathAsync(
        string canonicalPath,
        ReadOnlyMemory<byte> content,
        int depth,
        CancellationToken cancellationToken)
    {
        using var storage = budget.UseExecutionStorage();
        if (!_loading.Add(canonicalPath))
            throw new JqException("circular import of " + canonicalPath);
        try
        {
            string sourceText = DecodeModuleSource(canonicalPath, content);
            var programSource = new JqProgramSource("file \"" + canonicalPath + "\"", canonicalPath);
            // Pre-scan imports before loading dependencies.
            var tokens = Lexer.Tokenize(sourceText, programSource, budget);
            var preParser = new JqParser(tokens, programSource, rootEnvironment, budget);
            (_, IReadOnlyList<JqModuleImport> imports, int bodyIndex) = preParser.ParsePrefix();
            string childImporterDir = ParentDir(canonicalPath);
            JqEnvironment env = rootEnvironment;
            foreach (JqModuleImport import in imports)
                env = await ApplyImportAsync(env, import, childImporterDir, depth + 1, cancellationToken).ConfigureAwait(false);
            var definitions = new JqParser(tokens, programSource, env, budget).ParseDefinitions(bodyIndex);
            foreach (JqFunctionDefinition definition in definitions)
            {
                budget.ReserveMetadata(128, 1);
                env = env.ExtendFunction(definition.Name, definition.Arity, definition);
            }
            var exports = new Dictionary<(string Name, int Arity), JqUserClosure>();
            env.CollectFunctionClosures(exports, budget);
            var loaded = new LoadedModule(env, exports);
            _cache[canonicalPath] = loaded;
            return loaded;
        }
        finally
        {
            _loading.Remove(canonicalPath);
        }
    }

    private IEnumerable<JqPath> ModulePaths(
        JqModuleImport import,
        string importerDir,
        string suffix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string rel = import.RelPath;
        int baseStart = rel.LastIndexOf((char)47) + 1;
        foreach (string dir in SearchDirs())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dir.Length == 0)
                continue;
            for (int variant = 0; variant < 3; variant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = dir.Length + (dir == "/" ? 0L : 1L) + rel.Length + suffix.Length
                    + (variant == 1 ? 8L : variant == 2 ? 1L + rel.Length - baseStart : 0L);
                // Cover path normalization and overlapping strings before any copy.
                budget.ReserveBytes(128 + 64L * length);
                string prefix = dir == "/" ? "/" : dir + "/";
                string candidate = variant switch
                {
                    0 => prefix + rel + suffix,
                    1 => prefix + rel + "/jq/main" + suffix,
                    _ => prefix + rel + "/" + rel[baseStart..] + suffix,
                };
                JqPath path;
                try
                {
                    path = new JqPath(candidate);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                yield return path;
            }
        }

        IEnumerable<string> SearchDirs()
        {
            if (import.Metadata is not null && import.Metadata.TryGetPropertyValue("search", out JsonNode? search))
            {
                if (search is JsonArray array)
                {
                    foreach (JsonNode? item in array)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Resolve(item) is string dir) yield return dir;
                    }
                }
                else
                {
                    if (Resolve(search) is string dir) yield return dir;
                }
                yield break;
            }
            else yield return importerDir;
            foreach (string lib in libraryDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return lib;
            }
        }

        string? Resolve(JsonNode? node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out string? entry)
                || entry.Length == 0) return null;
            if (entry == ".") return importerDir;
            if (entry.StartsWith("~/", StringComparison.Ordinal) || entry == "~"
                || entry.StartsWith("$ORIGIN/", StringComparison.Ordinal) || entry == "$ORIGIN") return null;
            budget.ReserveBytes(128 + 64L * (entry.Length + (long)importerDir.Length + 1));
            string combined = entry.StartsWith((char)47) ? entry
                : importerDir == "/" ? "/" + entry : importerDir + "/" + entry;
            if (!JqRawPath.TryParse(combined, out JqRawPath? raw)) return null;
            return raw.TryToAbsolute(JqPath.Root, out JqPath? absolute) ? absolute.Path : null;
        }
    }

    private async Task<JqReadResult?> TryReadModuleFileAsync(JqPath path, CancellationToken cancellationToken)
    {
        var opened = await JqHostExtensions.GuardedOpenAsync(host, path, cancellationToken).ConfigureAwait(false);
        if (opened.FileDescriptor is not { } descriptor) return null;
        bool failed = false;
        JqReadResult? content = null;
        try
        {
            content = await host.TryReadAllBytesAsync(descriptor, budget, cancellationToken).ConfigureAwait(false);
            failed = content is JqReadResult.Failed;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try { await host.CloseOwnedDescriptorAsync(descriptor, path.Path, failed).ConfigureAwait(false); }
            catch { (content as JqReadResult.Complete)?.Dispose(); throw; }
        }
        return content;
    }

    private static string ParentDir(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        int slash = canonicalPath.LastIndexOf((char)47);
        if (slash <= 0)
            return "/";
        return canonicalPath[..slash];
    }

    internal async Task<JsonObject> GetModuleMetadataAsync(string relPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        string? validation = JqModulePathValidation.Validate(relPath, budget);
        if (validation is not null)
            throw new JqException(validation);
        var probe = new JqModuleImport(relPath, null, false, null);
        foreach (JqPath path in ModulePaths(probe, workingDirectory, ".jq", cancellationToken))
        {
            var result = await TryReadModuleFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            if (result is JqReadResult.Failed)
                throw new JqException("error loading module " + path.Path + ": input read failed");
            using var content = (JqReadResult.Complete)result;
            string source = DecodeModuleSource(path.Path, content.Content);
            var programSource = JqProgramSource.File(path.Path);
            var parser = new JqParser(source, programSource, rootEnvironment, budget);
            var (metadata, imports, definitions) = parser.InspectLibrary();
            return BuildMetadataObject(metadata, imports, definitions);
        }
        throw new JqException("module not found: " + relPath);
    }

    private string DecodeModuleSource(string path, ReadOnlyMemory<byte> content)
    {
        budget.ReserveString(Encoding.UTF8.GetCharCount(content.Span));
        try { return Helpers.Utf8Text.Decode(content); }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        { throw new JqException("error loading module " + path + ": " + exception.Message); }
    }

    private JsonObject BuildMetadataObject(JsonObject? metadata, IReadOnlyList<JqModuleImport> imports,
        IReadOnlyList<JqFunctionDefinition> definitions)
    {
        budget.ReserveBytes(192);
        JsonObject baseObject = metadata is not null
            ? (JsonObject)budget.CopyValue(metadata, 0)
            : new JsonObject();
        var deps = new JsonArray();
        foreach (JqModuleImport import in imports)
        {
            budget.CheckCancellation();
            budget.ReserveBytes(64 + 160 * 3);
            JsonObject dep = import.Metadata is not null
                ? (JsonObject)budget.CopyValue(import.Metadata, 2)
                : new JsonObject();
            dep["relpath"] = JsonValue.Create(import.RelPath);
            if (import.Alias is not null)
                dep["as"] = JsonValue.Create(import.Alias);
            dep["is_data"] = JsonValue.Create(import.IsData);
            budget.CheckCancellation();
            budget.ReserveString(import.RelPath.Length + (import.Alias?.Length ?? 0) + 16);
            deps.Add(dep);
        }
        var defs = new JsonArray();
        foreach (JqFunctionDefinition definition in definitions)
        {
            budget.CheckCancellation();
            budget.ReserveBytes(128 + 2L * (definition.Name.Length + 12));
            string entry = definition.Name + "/" + definition.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            budget.CheckCancellation();
            budget.ReserveString(entry.Length);
            defs.Add(JsonValue.Create(entry));
        }
        baseObject["deps"] = deps;
        baseObject["defs"] = defs;
        return baseObject;
    }
}
