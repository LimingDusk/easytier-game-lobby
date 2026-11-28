using System.Text.Json;
using GameLobby;

if (args.Length != 3 || !ReleaseVersion.TryParse(args[1], out _))
{
    Console.Error.WriteLine("Usage: UpdateManifest <release EXE> <tag> <output JSON>");
    return 1;
}

using var executable = File.OpenRead(args[0]);
var manifest = UpdateBlockManifest.Create(executable, args[1]);
byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
if (UpdateBlockManifest.TryRead(json, new UpdatePackage(manifest.Tag, manifest.Size, manifest.Sha256)) is null)
    throw new InvalidDataException("The generated manifest cannot be read by the updater.");

File.WriteAllBytes(args[2], json);
Console.WriteLine($"{manifest.Tag}: {manifest.Blocks.Count} blocks, {json.Length} manifest bytes, SHA-256 {manifest.Sha256}");
return 0;
