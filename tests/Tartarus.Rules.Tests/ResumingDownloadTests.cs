using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

/// <summary>The download against a local server that cuts connections, stalls, ignores Range.</summary>
public sealed class ResumingDownloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tartarus-download-").FullName;
    private readonly HttpClient _http = new();
    private static readonly byte[] Body = RandomNumberGenerator.GetBytes(300_000);
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
    private string Zip => Path.Combine(_dir, "a.zip");
    private string Part => ResumingDownload.PartOf(Zip);

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>What the server does on a request: how far into the body it sends, and how it ends.</summary>
    private sealed record Answer(long Until, bool Stall = false, bool IgnoreRange = false, int Status = 0);

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _body;
        private readonly Func<int, Answer> _answer;
        public List<long> From { get; } = [];
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/a.zip";

        public Server(byte[] body, Func<int, Answer> answer)
        {
            _body = body;
            _answer = answer;
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            try
            {
                while (true) _ = Serve(await _listener.AcceptTcpClientAsync(_stop.Token));
            }
            catch (OperationCanceledException) { }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, _stop.Token) > 0) head.Append((char)one[0]);
                    long from = 0;
                    foreach (var line in head.ToString().Split("\r\n"))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) from = long.Parse(line["Range: bytes=".Length..].TrimEnd('-'));
                    Answer answer;
                    lock (From)
                    {
                        answer = _answer(From.Count);
                        From.Add(from);
                    }
                    if (answer.Status != 0)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {answer.Status} No\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                        return;
                    }
                    if (answer.IgnoreRange) from = 0;
                    string status = from > 0 ? $"206 Partial Content\r\nContent-Range: bytes {from}-{_body.Length - 1}/{_body.Length}" : "200 OK";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {_body.Length - from}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    long until = Math.Min(answer.Until, _body.Length);
                    if (until > from) await stream.WriteAsync(_body.AsMemory((int)from, (int)(until - from)), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    if (answer.Stall) await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private Task Run(Server server, byte[] body, int idleTries = 3, double stallSeconds = 5, List<Exception>? failures = null) =>
        ResumingDownload.UntilWholeAsync(_http, server.Url, Zip, body.Length, Sha(body), TimeSpan.FromSeconds(stallSeconds), idleTries, TimeSpan.FromMilliseconds(1),
            _ => { }, ex => failures?.Add(ex), CancellationToken.None);

    [Fact]
    public async Task CutOff_GoesOnFromWhereItStopped()
    {
        using var server = new Server(Body, n => new Answer(n == 0 ? 120_000 : long.MaxValue));
        var failures = new List<Exception>();
        await Run(server, Body, failures: failures);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.False(File.Exists(Part));
        Assert.Equal([0L, 120_000L], server.From);
        Assert.Single(failures);
    }

    [Fact]
    public async Task HostIgnoringRange_SendsItAllAgain()
    {
        File.WriteAllBytes(Part, Body[..50_000]);
        using var server = new Server(Body, _ => new Answer(long.MaxValue, IgnoreRange: true));
        await Run(server, Body);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.Equal([50_000L], server.From);
    }

    [Fact]
    public async Task WrongPart_IsThrownAway_AndComesWhole()
    {
        File.WriteAllBytes(Part, new byte[50_000]);
        using var server = new Server(Body, _ => new Answer(long.MaxValue));
        await Run(server, Body);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.Equal([50_000L, 0L], server.From);
    }

    [Fact]
    public async Task PartTheHostDoesNotHave_ComesWhole()
    {
        File.WriteAllBytes(Part, Body[..50_000]);
        using var server = new Server(Body, n => new Answer(long.MaxValue, Status: n == 0 ? 416 : 0));
        await Run(server, Body);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.Equal([50_000L, 0L], server.From);
    }

    [Fact]
    public async Task TriesThatBringNothing_GiveUp_AndKeepThePart()
    {
        File.WriteAllBytes(Part, Body[..50_000]);
        using var server = new Server(Body, _ => new Answer(0));
        await Assert.ThrowsAnyAsync<Exception>(() => Run(server, Body, idleTries: 3));
        Assert.Equal(3, server.From.Count);
        Assert.Equal(50_000, new FileInfo(Part).Length);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task TriesThatBringBytes_DoNotCount()
    {
        using var server = new Server(Body, n => new Answer((n + 1) * 30_000L));
        await Run(server, Body, idleTries: 2);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.Equal(10, server.From.Count);
    }

    [Fact]
    public async Task Stalled_IsGivenUp_AndGoesOn()
    {
        using var server = new Server(Body, n => new Answer(n == 0 ? 70_000 : long.MaxValue, Stall: n == 0));
        var failures = new List<Exception>();
        await Run(server, Body, stallSeconds: 0.3, failures: failures);
        Assert.Equal(Body, File.ReadAllBytes(Zip));
        Assert.Equal([0L, 70_000L], server.From);
        Assert.IsType<TimeoutException>(Assert.Single(failures));
    }

    [Fact]
    public async Task WholeAndWrong_ThrowsAtOnce()
    {
        var other = Body.ToArray();
        other[0] ^= 1;
        using var server = new Server(other, _ => new Answer(long.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(server, Body));
        Assert.Single(server.From);
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task PlayersCancel_ThrowsAtOnce()
    {
        using var server = new Server(Body, _ => new Answer(10_000, Stall: true));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResumingDownload.UntilWholeAsync(_http, server.Url, Zip, Body.Length, Sha(Body),
            TimeSpan.FromSeconds(30), 3, TimeSpan.FromMilliseconds(1), _ => { }, null, cancel.Token));
        Assert.Single(server.From);
    }
}
