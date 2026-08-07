using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using Google.Protobuf;

namespace FXViewer;

public sealed class CTraderClient : IAsyncDisposable
{
    private const string DemoHost = "demo.ctraderapi.com";
    private const string LiveHost = "live.ctraderapi.com";
    private const int Port = 5035;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, TaskCompletionSource<ProtoMessage>> _pending = new();
    private long _nextMsgId;
    private bool _disposed;

    public event Action<string>? Log;
    public event Action<ProtoOASpotEvent>? SpotReceived;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(bool live, CancellationToken ct)
    {
        var host = live ? LiveHost : DemoHost;
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, Port, ct);
        _ssl = new SslStream(_tcp.GetStream());
        await _ssl.AuthenticateAsClientAsync(host);
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        Log?.Invoke($"Connected to {host}:{Port}");
    }

    public async Task ApplicationAuthAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var req = new ProtoOAApplicationAuthReq { ClientId = clientId, ClientSecret = clientSecret };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        ParseResponse(msg, ProtoOAApplicationAuthRes.Parser, ProtoOAPayloadType.ProtoOaApplicationAuthRes);
        Log?.Invoke("Application authorized");
    }

    public async Task<IReadOnlyList<ProtoOACtidTraderAccount>> GetAccountListAsync(string accessToken, CancellationToken ct)
    {
        var req = new ProtoOAGetAccountListByAccessTokenReq { AccessToken = accessToken };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        var res = ParseResponse(msg, ProtoOAGetAccountListByAccessTokenRes.Parser,
            ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenRes);
        return res.CtidTraderAccount;
    }

    public async Task AccountAuthAsync(long accountId, string accessToken, CancellationToken ct)
    {
        var req = new ProtoOAAccountAuthReq { CtidTraderAccountId = accountId, AccessToken = accessToken };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        ParseResponse(msg, ProtoOAAccountAuthRes.Parser, ProtoOAPayloadType.ProtoOaAccountAuthRes);
        Log?.Invoke($"Account {accountId} authorized");
    }

    public async Task<IReadOnlyList<ProtoOALightSymbol>> GetSymbolsAsync(long accountId, CancellationToken ct)
    {
        var req = new ProtoOASymbolsListReq { CtidTraderAccountId = accountId };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        var res = ParseResponse(msg, ProtoOASymbolsListRes.Parser, ProtoOAPayloadType.ProtoOaSymbolsListRes);
        return res.Symbol;
    }

    public async Task SubscribeSpotsAsync(long accountId, IEnumerable<long> symbolIds, CancellationToken ct)
    {
        var req = new ProtoOASubscribeSpotsReq { CtidTraderAccountId = accountId };
        var ids = symbolIds.ToList();
        foreach (var id in ids) req.SymbolId.Add(id);
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        ParseResponse(msg, ProtoOASubscribeSpotsRes.Parser, ProtoOAPayloadType.ProtoOaSubscribeSpotsRes);
        Log?.Invoke($"Subscribed to spots: {string.Join(",", ids)}");
    }

    public async Task<ProtoOAGetTrendbarsRes> GetTrendbarsAsync(
        long accountId, long symbolId, ProtoOATrendbarPeriod period, long fromMs, long toMs, CancellationToken ct)
    {
        var req = new ProtoOAGetTrendbarsReq
        {
            CtidTraderAccountId = accountId,
            SymbolId = symbolId,
            Period = period,
            FromTimestamp = fromMs,
            ToTimestamp = toMs
        };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        return ParseResponse(msg, ProtoOAGetTrendbarsRes.Parser, ProtoOAPayloadType.ProtoOaGetTrendbarsRes);
    }

    public async Task<ProtoOADealListRes> GetDealsAsync(
        long accountId, long fromMs, long toMs, CancellationToken ct)
    {
        var req = new ProtoOADealListReq
        {
            CtidTraderAccountId = accountId,
            FromTimestamp = fromMs,
            ToTimestamp = toMs
        };
        var msg = await RequestAsync((uint)req.PayloadType, req, ct);
        return ParseResponse(msg, ProtoOADealListRes.Parser, ProtoOAPayloadType.ProtoOaDealListRes);
    }

    private async Task<ProtoMessage> RequestAsync(uint payloadType, IMessage req, CancellationToken ct)
    {
        if (_ssl == null) throw new InvalidOperationException("Not connected");
        var id = Interlocked.Increment(ref _nextMsgId).ToString();
        var tcs = new TaskCompletionSource<ProtoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[id] = tcs;
        try
        {
            var envelope = new ProtoMessage { PayloadType = payloadType, Payload = req.ToByteString(), ClientMsgId = id };
            await SendFrameAsync(envelope, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            using var reg = timeoutCts.Token.Register(
                () => tcs.TrySetException(new TimeoutException("No response from server")));
            return await tcs.Task;
        }
        finally
        {
            lock (_pendingLock) _pending.Remove(id);
        }
    }

    private static T ParseResponse<T>(ProtoMessage msg, MessageParser<T> parser, ProtoOAPayloadType expected)
        where T : IMessage<T>
    {
        if (msg.PayloadType == (uint)ProtoOAPayloadType.ProtoOaErrorRes)
        {
            var err = ProtoOAErrorRes.Parser.ParseFrom(msg.Payload);
            throw new InvalidOperationException($"{err.ErrorCode}: {err.Description}");
        }
        if (msg.PayloadType == (uint)ProtoPayloadType.ErrorRes)
        {
            var err = ProtoErrorRes.Parser.ParseFrom(msg.Payload);
            throw new InvalidOperationException($"{err.ErrorCode}: {err.Description}");
        }
        if (msg.PayloadType != (uint)expected)
            throw new InvalidOperationException($"Unexpected payload type {msg.PayloadType}, expected {(uint)expected}");
        return parser.ParseFrom(msg.Payload);
    }

    private async Task SendFrameAsync(ProtoMessage msg, CancellationToken ct)
    {
        var payload = msg.ToByteArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ssl!.WriteAsync(frame, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _ssl!.ReadExactlyAsync(lenBuf, ct);
                var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (len <= 0 || len > 64 * 1024 * 1024)
                    throw new InvalidDataException($"Bad frame length {len}");
                var buf = new byte[len];
                await _ssl.ReadExactlyAsync(buf, ct);
                Dispatch(ProtoMessage.Parser.ParseFrom(buf));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            FailAllPending(ex);
            Disconnected?.Invoke(ex.Message);
        }
    }

    private void Dispatch(ProtoMessage msg)
    {
        if (msg.HasClientMsgId)
        {
            TaskCompletionSource<ProtoMessage>? tcs;
            lock (_pendingLock) _pending.Remove(msg.ClientMsgId, out tcs);
            if (tcs != null)
            {
                tcs.TrySetResult(msg);
                return;
            }
        }
        switch (msg.PayloadType)
        {
            case (uint)ProtoOAPayloadType.ProtoOaSpotEvent:
                SpotReceived?.Invoke(ProtoOASpotEvent.Parser.ParseFrom(msg.Payload));
                break;
            case (uint)ProtoPayloadType.HeartbeatEvent:
                break;
            case (uint)ProtoOAPayloadType.ProtoOaErrorRes:
                var oaErr = ProtoOAErrorRes.Parser.ParseFrom(msg.Payload);
                Log?.Invoke($"Server error {oaErr.ErrorCode}: {oaErr.Description}");
                break;
            case (uint)ProtoPayloadType.ErrorRes:
                var err = ProtoErrorRes.Parser.ParseFrom(msg.Payload);
                Log?.Invoke($"Server error {err.ErrorCode}: {err.Description}");
                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var heartbeat = new ProtoMessage
        {
            PayloadType = (uint)ProtoPayloadType.HeartbeatEvent,
            Payload = new ProtoHeartbeatEvent().ToByteString()
        };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await SendFrameAsync(heartbeat, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log?.Invoke("Heartbeat stopped: " + ex.Message);
        }
    }

    private void FailAllPending(Exception ex)
    {
        List<TaskCompletionSource<ProtoMessage>> list;
        lock (_pendingLock)
        {
            list = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in list) tcs.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        foreach (var task in new[] { _readLoop, _heartbeatLoop })
        {
            if (task == null) continue;
            try { await task; } catch { }
        }
        FailAllPending(new ObjectDisposedException(nameof(CTraderClient)));
        _cts?.Dispose();
    }
}
