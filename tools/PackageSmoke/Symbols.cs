using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Lokad.Jq;

// Development-only inspection of the packed artifact. This fixture is never
// part of the runtime, ordinary test suite or automatic CI.
internal static class PackageSymbols
{
    internal static void Verify(string symbolPackage, string sourceRoot, string commit)
    {
        using var archive = ZipFile.OpenRead(symbolPackage);
        var entry = archive.GetEntry("lib/net10.0/Lokad.Jq.pdb")
            ?? throw new InvalidOperationException("Missing packaged portable PDB.");
        using var pdbStream = new MemoryStream();
        using (var packedStream = entry.Open()) packedStream.CopyTo(pdbStream);
        pdbStream.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader reader = provider.GetMetadataReader();
        var header = reader.DebugMetadataHeader ?? throw new InvalidOperationException("Missing PDB header.");
        var id = new BlobContentId(header.Id);
        using var assemblyStream = File.OpenRead(typeof(Jq).Assembly.Location);
        using var assembly = new PEReader(assemblyStream);
        var codeViewEntry = assembly.ReadDebugDirectory().Single(value => value.Type == DebugDirectoryEntryType.CodeView);
        if (assembly.ReadCodeViewDebugDirectoryData(codeViewEntry).Guid != id.Guid || codeViewEntry.Stamp != id.Stamp)
            throw new InvalidOperationException("Symbol package does not match the restored assembly.");

        var sourceLinkKind = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
        var embeddedKind = new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
        var sourceLink = reader.CustomDebugInformation.Select(reader.GetCustomDebugInformation)
            .Single(value => reader.GetGuid(value.Kind) == sourceLinkKind);
        using var links = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
        var mapping = links.RootElement.GetProperty("documents").EnumerateObject().Single();
        string expectedUrl = "https://raw.githubusercontent.com/lokad/Jq/" + commit + "/";
        if (mapping.Name != "/_/*" || mapping.Value.GetString() != expectedUrl + "*")
            throw new InvalidOperationException("SourceLink must map normalized sources to the expected public commit.");

        int tracked = 0, embedded = 0;
        foreach (DocumentHandle handle in reader.Documents)
        {
            Document document = reader.GetDocument(handle);
            string name = reader.GetString(document.Name);
            if (!name.StartsWith("/_/", StringComparison.Ordinal))
                throw new InvalidOperationException("A symbol document exposes an unnormalized local path.");
            byte[] source;
            var embeddedSources = reader.GetCustomDebugInformation(handle).Select(reader.GetCustomDebugInformation)
                .Where(value => reader.GetGuid(value.Kind) == embeddedKind).ToArray();
            if (embeddedSources.Length != 0)
            {
                byte[] blob = reader.GetBlobBytes(embeddedSources.Single().Value);
                using var encoded = new MemoryStream(blob);
                using var sizeReader = new BinaryReader(encoded, System.Text.Encoding.UTF8, true);
                int size = sizeReader.ReadInt32();
                if (size == 0) source = blob[sizeof(int)..];
                else
                {
                    using var inflater = new DeflateStream(encoded, CompressionMode.Decompress);
                    using var decoded = new MemoryStream();
                    inflater.CopyTo(decoded);
                    source = decoded.ToArray();
                    if (source.Length != size) throw new InvalidOperationException("Invalid embedded source size.");
                }
                embedded++;
            }
            else
            {
                // Compare with the committed blob, rather than accepting dirty
                // files or a checkout whose line endings differ from GitHub.
                var start = new ProcessStartInfo("git")
                {
                    WorkingDirectory = sourceRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("show");
                start.ArgumentList.Add(commit + ":" + name[3..]);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
                using var content = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(content);
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("Source blob is unavailable: " + error);
                source = content.ToArray();
                tracked++;
            }
            if (reader.GetGuid(document.HashAlgorithm) != new Guid("8829d00f-11b8-4213-878b-770e8597ac16")
                || !SHA256.HashData(source).AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)))
                throw new InvalidOperationException("Source checksum does not match the committed or embedded bytes: " + name);
        }
        if (tracked == 0) throw new InvalidOperationException("No committed source documents were verified.");
        Console.WriteLine($"Symbols: assembly identity, public commit mappings and SHA-256 checksums pass for {tracked} committed and {embedded} embedded documents. Hosted URL resolution remains separate.");
    }
}
