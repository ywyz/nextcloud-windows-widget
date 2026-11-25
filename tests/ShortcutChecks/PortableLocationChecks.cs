using NextcloudWidget.App;
using NextcloudWidget.Updates;

internal static class PortableLocationChecks
{
    internal static void Run(string root)
    {
        string source = Path.Combine(root, "NextcloudWidget-win-x64 (1).exe");
        string folder = Directory.CreateDirectory(Path.Combine(root, "Selected Folder 新")).FullName;
        string destination = Path.Combine(folder, ReleaseInfo.Asset);
        byte[] payload = [0x4d, 0x5a, 0x11, 0xfe];
        File.WriteAllBytes(source, payload);

        // Two simultaneous requests must leave one complete canonical copy.
        Parallel.For(0, 2, _ =>
        {
            if (PortableLocation.CopyToFolder(source, folder) != destination)
                throw new InvalidOperationException("Unexpected portable executable path.");
        });
        if (!File.ReadAllBytes(source).SequenceEqual(payload) || !File.ReadAllBytes(destination).SequenceEqual(payload))
            throw new InvalidOperationException("The browser-renamed download and its copy must retain their bytes.");
        if (PortableLocation.CopyToFolder(destination, folder) != destination ||
            PortableLocation.CopyToFolder(source, folder) != destination)
            throw new InvalidOperationException("Reselecting the current or an identical copy must be idempotent.");

        byte[] collision = [0x4d, 0x5a, 0x22, 0xfe];
        File.WriteAllBytes(destination, collision);
        try
        {
            PortableLocation.CopyToFolder(source, folder);
            throw new InvalidOperationException("A conflicting destination must be refused.");
        }
        catch (IOException) { }
        if (!File.ReadAllBytes(destination).SequenceEqual(collision) || !Directory.GetFiles(folder).SequenceEqual([destination]))
            throw new InvalidOperationException("A conflict must preserve the existing file and leave no temporary files.");

        Console.WriteLine("PASS portable copy, concurrent selection, canonical name, same file, identical copy and collision");
    }
}
