using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PocketDrop.Core;
using PocketDrop.Transport;

// Usage: PocketDrop.TestReceiver <receive-folder> [seconds-to-run]
var folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pocketdrop-e2e");
var seconds = args.Length > 1 ? int.Parse(args[1]) : 120;
Directory.CreateDirectory(folder);

var settings = new Settings { BaseFolder = folder, DeviceName = "E2E-PC" };
var ui = new AutoAcceptUi();
var manager = new TransferManager(() => settings, ui);
var handler = new StreamProtocolHandler(manager, () => settings);

// POCKETDROP_REMOTE_KEY=<32 hex> POCKETDROP_REMOTE_PORT=<port>: only the key-protected port a tunnel would use.
var remoteKey = Environment.GetEnvironmentVariable("POCKETDROP_REMOTE_KEY");
if (!string.IsNullOrEmpty(remoteKey))
{
    var port = int.Parse(Environment.GetEnvironmentVariable("POCKETDROP_REMOTE_PORT") ?? AppInfo.RemotePort.ToString());
    await using var remoteWeb = new WebServer(manager, () => settings, 0, port, () => remoteKey);
    await remoteWeb.StartAsync();
    Console.WriteLine($"READY remote=127.0.0.1:{port} folder={folder}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    return;
}

using var tcp = new TcpTransport(handler);
tcp.Start();
using var discovery = new DiscoveryService(() => settings);
discovery.Start();
await using var web = new WebServer(manager, () => settings);
await web.StartAsync();

Console.WriteLine($"READY tcp={AppInfo.TcpPort} http={AppInfo.HttpPort} folder={folder}");
await Task.Delay(TimeSpan.FromSeconds(seconds));

internal sealed class AutoAcceptUi : IReceiverUi
{
    public Task<bool> ConfirmAsync(TransferOffer offer, string folderName, CancellationToken ct)
    {
        Console.WriteLine($"OFFER from {offer.Device} via {offer.Transport}: {offer.Files.Count} files → {folderName}");
        // "Decline me" lets the test exercise the reject path.
        return Task.FromResult(folderName != "Decline me");
    }

    public void TransferStarted(ReceiveSession session) => Console.WriteLine($"START {session.FolderPath}");

    public void TransferFinished(ReceiveSession session) =>
        Console.WriteLine($"FINISHED {session.State} saved={session.SavedCount} {session.Error}");
}
