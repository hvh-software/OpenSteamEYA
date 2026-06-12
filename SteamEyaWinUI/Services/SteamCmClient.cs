using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamEyaWinUI.Services;

internal enum SteamEresult
{
    Ok = 1,
    InvalidPassword = 5,
    InvalidParam = 8,
    AccessDenied = 15,
    Revoked = 26,
    Expired = 27,
    AccountLogonDenied = 63
}

internal sealed class SteamCmException : InvalidOperationException
{
    public SteamCmException(int result)
        : base(GetMessage(result))
    {
        Result = result;
    }

    public int Result { get; }

    public bool IsTokenFailure => Result is
        (int)SteamEresult.InvalidPassword or
        (int)SteamEresult.InvalidParam or
        (int)SteamEresult.AccessDenied or
        (int)SteamEresult.Revoked or
        (int)SteamEresult.Expired or
        (int)SteamEresult.AccountLogonDenied;

    private static string GetMessage(int result)
    {
        return result switch
        {
            (int)SteamEresult.Revoked =>
                "EYA 令牌已被 Steam 拒绝，可能已被手动撤销。",
            (int)SteamEresult.Expired =>
                "EYA 令牌已被 Steam 判定为过期。",
            (int)SteamEresult.InvalidPassword or
            (int)SteamEresult.InvalidParam or
            (int)SteamEresult.AccessDenied or
            (int)SteamEresult.AccountLogonDenied =>
                "EYA 令牌已被 Steam 拒绝，可能已被撤销或失效。",
            _ => $"Steam 登录失败：EResult={result}"
        };
    }
}

internal sealed class SteamCmClient : IAsyncDisposable
{
    private const uint ProtoMask = 0x80000000;
    private const ulong JobIdNone = ulong.MaxValue;
    private const int ProtocolVersion = 65580;
    private const uint LogonId = 0xA1A2A3A4;
    private const int ClientOsWindows10 = 16;
    private const int WebSocketBufferSize = 64 * 1024;
    private const int RequestTimeoutMs = 15_000;

    private const int EMsgMulti = 1;
    private const int EMsgServiceMethodCallFromClient = 151;
    private const int EMsgClientHeartBeat = 703;
    private const int EMsgClientLogOff = 706;
    private const int EMsgClientLogOnResponse = 751;
    private const int EMsgClientLoggedOff = 757;
    private const int EMsgClientLogon = 5514;
    private const int EMsgClientGamesPlayedWithDataBlob = 5410;
    private const int EMsgClientToGC = 5452;
    private const int EMsgClientFromGC = 5453;
    private const int MaxCachedGcMessagesPerKey = 8;
    private const int MaxCachedGcMessageKeys = 64;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ServiceMethodResponse>> _jobs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SteamGcClientMessage>> _gcWaiters = new();
    private readonly Dictionary<string, Queue<SteamGcClientMessage>> _gcMessageCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cacheableGcMessageKeys = new(StringComparer.Ordinal);
    private readonly object _gcMessagesLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancellation = new();

    private ClientWebSocket? _socket;
    private Task? _receiveTask;
    private Timer? _heartbeatTimer;
    private TaskCompletionSource<LogonResponse>? _logonResponse;
    private int _sessionId;
    private ulong _steamId;
    private ulong _currentJobId = (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue);

    public SteamCmClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ConnectAndLogOnAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        _steamId = ulong.Parse(steamId, CultureInfo.InvariantCulture);

        var servers = await GetCmServersAsync(cancellationToken);
        Exception? lastError = null;

        foreach (var server in servers.Take(8))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ConnectAsync(server, cancellationToken);
                await LogOnAsync(refreshToken, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is SteamCmException cmException && cmException.IsTokenFailure)
                {
                    throw;
                }

                lastError = ex;
                await DisconnectSocketAsync();
            }
        }

        throw new InvalidOperationException(
            $"无法连接到 Steam 服务器。{lastError?.Message}",
            lastError);
    }

    public async Task<string> GenerateAccessTokenForAppAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteString(1, refreshToken);
            writer.WriteFixed64(2, _steamId);
            writer.WriteInt32(3, 0);
        });

        var response = await SendServiceMethodAsync(
            "Authentication.GenerateAccessTokenForApp#1",
            requestBody,
            realm: 1,
            cancellationToken);

        EnsureOk(response, "获取 Web access token 失败");

        var accessToken = DecodeAccessTokenForAppResponse(response.Body);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Steam 返回的 Web access token 为空。");
        }

        return accessToken;
    }

    public async Task<SteamEresult> UnsubscribePublishedFileAsync(
        ulong publishedFileId,
        uint appId,
        int listType,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt64(1, publishedFileId);
            writer.WriteUInt32(2, (uint)listType);
            writer.WriteInt32(3, (int)appId);
            writer.WriteBool(4, true);
        });

        var response = await SendServiceMethodAsync(
            "PublishedFile.Unsubscribe#1",
            requestBody,
            realm: null,
            cancellationToken);

        return (SteamEresult)response.Result;
    }

    public async Task SetGamesPlayedAsync(
        IReadOnlyList<uint> appIds,
        CancellationToken cancellationToken)
    {
        var body = SteamProtoWriter.Build(writer =>
        {
            foreach (var appId in appIds)
            {
                writer.WriteBytes(1, SteamProtoWriter.Build(game =>
                    game.WriteFixed64(2, appId)));
            }

            writer.WriteUInt32(2, ClientOsWindows10);
        });

        await SendProtobufMessageAsync(
            EMsgClientGamesPlayedWithDataBlob,
            body,
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken);
    }

    public async Task SendGcProtobufMessageAsync(
        uint appId,
        uint msgType,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var gcPayload = BuildGcProtobufPayload(msgType, payload);
        var body = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, appId);
            writer.WriteUInt32(2, msgType | ProtoMask);
            writer.WriteBytes(3, gcPayload);
        });

        await SendProtobufMessageAsync(
            EMsgClientToGC,
            body,
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken,
            routingAppId: appId);
    }

    public async Task<SteamGcClientMessage> WaitForGcMessageAsync(
        uint appId,
        uint msgType,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool cacheUnmatched = false)
    {
        var key = GetGcWaiterKey(appId, msgType);
        var completion = new TaskCompletionSource<SteamGcClientMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gcMessagesLock)
        {
            if (cacheUnmatched)
            {
                _cacheableGcMessageKeys.Add(key);
            }

            if (TryDequeueCachedGcMessage(key, out var cachedMessage))
            {
                return cachedMessage;
            }

            if (!_gcWaiters.TryAdd(key, completion))
            {
                throw new InvalidOperationException("已有相同的 GC 消息等待任务。");
            }
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            lock (_gcMessagesLock)
            {
                _gcWaiters.TryRemove(key, out _);
            }
        }
    }

    public async Task<bool> IsPublishedFileSubscribedAsync(
        ulong publishedFileId,
        uint appId,
        int listType,
        CancellationToken cancellationToken)
    {
        var requestBody = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, appId);
            writer.WriteFixed64(2, publishedFileId);
            writer.WriteUInt32(3, (uint)listType);
        });

        var response = await SendServiceMethodAsync(
            "PublishedFile.AreFilesInSubscriptionList#1",
            requestBody,
            realm: null,
            cancellationToken);

        EnsureOk(response, "校验订阅状态失败");
        return DecodeAreFilesInSubscriptionListResponse(response.Body, publishedFileId);
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer?.Dispose();

        try
        {
            if (_socket?.State == WebSocketState.Open)
            {
                await SendProtobufMessageAsync(
                    EMsgClientLogOff,
                    Array.Empty<byte>(),
                    targetJobName: null,
                    jobIdSource: null,
                    realm: null,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort logoff; the socket is closed below either way.
        }

        _receiveCancellation.Cancel();
        await DisconnectSocketAsync();
        _receiveCancellation.Dispose();
        _sendLock.Dispose();
    }

    private async Task<List<string>> GetCmServersAsync(CancellationToken cancellationToken)
    {
        const string url = "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v0001/"
            + "?format=json&cellid=0&cmtype=websockets";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            !responseElement.TryGetProperty("serverlist", out var serverList))
        {
            throw new InvalidOperationException("Steam CM 列表响应格式不正确。");
        }

        var servers = serverList.EnumerateArray()
            .Where(server => GetString(server, "type") == "websockets" &&
                GetString(server, "realm") == "steamglobal")
            .OrderBy(server => GetDouble(server, "wtd_load"))
            .Select(server => GetString(server, "endpoint"))
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint!)
            .ToList();

        return servers.Count > 0
            ? servers
            : throw new InvalidOperationException("Steam 没有返回可用的 WebSocket CM。");
    }

    private async Task ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await _socket.ConnectAsync(new Uri($"wss://{endpoint}/cmsocket/"), cancellationToken);

        _receiveTask = Task.Run(
            () => ReceiveLoopAsync(_receiveCancellation.Token),
            CancellationToken.None);
    }

    private async Task LogOnAsync(string refreshToken, CancellationToken cancellationToken)
    {
        _logonResponse = new TaskCompletionSource<LogonResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await SendProtobufMessageAsync(
            EMsgClientLogon,
            EncodeClientLogon(refreshToken, _steamId),
            targetJobName: null,
            jobIdSource: null,
            realm: null,
            cancellationToken);

        var logon = await _logonResponse.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (logon.Result != (int)SteamEresult.Ok)
        {
            throw new SteamCmException(logon.Result);
        }

        if (logon.HeartbeatSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(logon.HeartbeatSeconds);
            _heartbeatTimer = new Timer(
                _ => _ = SendHeartbeatAsync(),
                null,
                interval,
                interval);
        }
    }

    private async Task<ServiceMethodResponse> SendServiceMethodAsync(
        string method,
        byte[] requestBody,
        uint? realm,
        CancellationToken cancellationToken)
    {
        var jobId = NextJobId();
        var completion = new TaskCompletionSource<ServiceMethodResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_jobs.TryAdd(jobId, completion))
        {
            throw new InvalidOperationException("Steam 请求队列创建失败。");
        }

        try
        {
            await SendProtobufMessageAsync(
                EMsgServiceMethodCallFromClient,
                requestBody,
                method,
                jobId,
                realm,
                cancellationToken);

            return await completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(RequestTimeoutMs),
                cancellationToken);
        }
        finally
        {
            _jobs.TryRemove(jobId, out _);
        }
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            if (_socket?.State != WebSocketState.Open)
            {
                return;
            }

            await SendProtobufMessageAsync(
                EMsgClientHeartBeat,
                Array.Empty<byte>(),
                targetJobName: null,
                jobIdSource: null,
                realm: null,
                CancellationToken.None);
        }
        catch
        {
            // Heartbeat failures surface through pending requests or socket close.
        }
    }

    private async Task SendProtobufMessageAsync(
        int eMsg,
        byte[] body,
        string? targetJobName,
        ulong? jobIdSource,
        uint? realm,
        CancellationToken cancellationToken,
        uint? routingAppId = null)
    {
        if (_socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Steam CM 尚未连接。");
        }

        var header = SteamProtoWriter.Build(writer =>
        {
            writer.WriteFixed64(1, _steamId);
            writer.WriteInt32(2, _sessionId);
            writer.WriteFixed64(10, jobIdSource ?? JobIdNone);
            writer.WriteFixed64(11, JobIdNone);

            if (routingAppId.HasValue)
            {
                writer.WriteUInt32(3, routingAppId.Value);
            }

            if (!string.IsNullOrWhiteSpace(targetJobName))
            {
                writer.WriteString(12, targetJobName);
            }

            if (realm.HasValue)
            {
                writer.WriteUInt32(32, realm.Value);
            }
        });

        var packet = new byte[8 + header.Length + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), (uint)eMsg | ProtoMask);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)header.Length);
        header.CopyTo(packet.AsSpan(8));
        body.CopyTo(packet.AsSpan(8 + header.Length));

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(
                packet,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[WebSocketBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                _socket?.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailPendingRequests(new InvalidOperationException("Steam CM 连接已关闭。"));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleNetMessage(message.ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailPendingRequests(ex);
        }
    }

    private void HandleNetMessage(byte[] packet)
    {
        if (packet.Length < 8)
        {
            return;
        }

        var rawMsg = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4));
        var eMsg = (int)(rawMsg & ~ProtoMask);
        var isProto = (rawMsg & ProtoMask) != 0;

        if (!isProto)
        {
            if (eMsg == EMsgClientLogOnResponse)
            {
                _logonResponse?.TrySetException(
                    new InvalidOperationException("Steam CM 返回了非 protobuf 登录响应。"));
            }

            return;
        }

        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        if (headerLength < 0 || packet.Length < 8 + headerLength)
        {
            return;
        }

        var header = DecodeProtoHeader(packet.AsSpan(8, headerLength).ToArray());
        var body = packet[(8 + headerLength)..];

        if (header.ClientSessionId.HasValue && header.ClientSessionId.Value != 0)
        {
            _sessionId = header.ClientSessionId.Value;
        }

        if (header.SteamId.HasValue && header.SteamId.Value != 0)
        {
            _steamId = header.SteamId.Value;
        }

        if (eMsg == EMsgMulti)
        {
            ProcessMulti(body);
            return;
        }

        if (header.JobIdTarget.HasValue &&
            header.JobIdTarget.Value != JobIdNone &&
            _jobs.TryRemove(header.JobIdTarget.Value, out var job))
        {
            job.TrySetResult(new ServiceMethodResponse(
                header.Result ?? (int)SteamEresult.Ok,
                header.ErrorMessage,
                body));
            return;
        }

        switch (eMsg)
        {
            case EMsgClientLogOnResponse:
                _logonResponse?.TrySetResult(DecodeLogonResponse(body));
                break;

            case EMsgClientLoggedOff:
                FailPendingRequests(new InvalidOperationException("Steam 账号已下线。"));
                break;

            case EMsgClientFromGC:
                HandleGcClientMessage(body);
                break;
        }
    }

    private void HandleGcClientMessage(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        uint? appId = null;
        uint? rawMsgType = null;
        byte[]? payload = null;

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    appId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    rawMsgType = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    payload = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (!appId.HasValue || !rawMsgType.HasValue || payload is null)
        {
            return;
        }

        var msgType = rawMsgType.Value & ~ProtoMask;
        var messageBody = ExtractGcMessageBody(rawMsgType.Value, payload);
        var message = new SteamGcClientMessage(appId.Value, msgType, messageBody);
        var key = GetGcWaiterKey(appId.Value, msgType);
        TaskCompletionSource<SteamGcClientMessage>? waiter = null;

        lock (_gcMessagesLock)
        {
            if (!_gcWaiters.TryRemove(key, out waiter))
            {
                if (_cacheableGcMessageKeys.Contains(key))
                {
                    EnqueueCachedGcMessage(key, message);
                }

                return;
            }
        }

        waiter.TrySetResult(message);
    }

    private bool TryDequeueCachedGcMessage(string key, out SteamGcClientMessage message)
    {
        if (_gcMessageCache.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            message = queue.Dequeue();
            if (queue.Count == 0)
            {
                _gcMessageCache.Remove(key);
            }

            return true;
        }

        message = default!;
        return false;
    }

    private void EnqueueCachedGcMessage(string key, SteamGcClientMessage message)
    {
        if (!_gcMessageCache.TryGetValue(key, out var queue))
        {
            if (_gcMessageCache.Count >= MaxCachedGcMessageKeys)
            {
                return;
            }

            queue = new Queue<SteamGcClientMessage>();
            _gcMessageCache[key] = queue;
        }

        while (queue.Count >= MaxCachedGcMessagesPerKey)
        {
            queue.Dequeue();
        }

        queue.Enqueue(message);
    }

    private void ProcessMulti(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        uint unzippedSize = 0;
        byte[]? payload = null;

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    unzippedSize = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    payload = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (payload is null)
        {
            return;
        }

        if (unzippedSize > 0)
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)unzippedSize);
            gzip.CopyTo(output);
            payload = output.ToArray();
        }

        var offset = 0;
        while (offset + 4 <= payload.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;

            if (size < 0 || offset + size > payload.Length)
            {
                break;
            }

            HandleNetMessage(payload[offset..(offset + size)]);
            offset += size;
        }
    }

    private async Task DisconnectSocketAsync()
    {
        try
        {
            if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
        }
        catch
        {
            _socket?.Abort();
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
        }
    }

    private ulong NextJobId()
    {
        var jobId = Interlocked.Increment(ref _currentJobId);
        return jobId == JobIdNone ? Interlocked.Increment(ref _currentJobId) : jobId;
    }

    private void FailPendingRequests(Exception ex)
    {
        _logonResponse?.TrySetException(ex);

        foreach (var (jobId, job) in _jobs)
        {
            if (_jobs.TryRemove(jobId, out _))
            {
                job.TrySetException(ex);
            }
        }

        List<TaskCompletionSource<SteamGcClientMessage>> gcWaiters;
        lock (_gcMessagesLock)
        {
            gcWaiters = _gcWaiters.Values.ToList();
            _gcWaiters.Clear();
            _gcMessageCache.Clear();
        }

        foreach (var waiter in gcWaiters)
        {
            waiter.TrySetException(ex);
        }
    }

    private static byte[] EncodeClientLogon(string refreshToken, ulong steamId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, ProtocolVersion);
            writer.WriteString(6, "english");
            writer.WriteUInt32(7, ClientOsWindows10);
            writer.WriteBool(8, true);
            writer.WriteBytes(11, SteamProtoWriter.Build(ip => ip.WriteFixed32(1, LogonId)));
            writer.WriteBytes(30, CreateMachineId(steamId.ToString(CultureInfo.InvariantCulture)));
            writer.WriteUInt32(33, 2);
            writer.WriteString(96, "unsub-all");
            writer.WriteBool(102, true);
            writer.WriteString(108, refreshToken);
        });
    }

    private static byte[] CreateMachineId(string accountIdentifier)
    {
        using var stream = new MemoryStream(155);
        WriteByte(stream, 0);
        WriteCString(stream, "MessageObject");
        WriteByte(stream, 1);
        WriteCString(stream, "BB3");
        WriteCString(stream, Sha1Hex($"SteamUser Hash BB3 {accountIdentifier}"));
        WriteByte(stream, 1);
        WriteCString(stream, "FF2");
        WriteCString(stream, Sha1Hex($"SteamUser Hash FF2 {accountIdentifier}"));
        WriteByte(stream, 1);
        WriteCString(stream, "3B3");
        WriteCString(stream, Sha1Hex($"SteamUser Hash 3B3 {accountIdentifier}"));
        WriteByte(stream, 8);
        WriteByte(stream, 8);
        return stream.ToArray();
    }

    private static byte[] BuildGcProtobufPayload(uint msgType, byte[] payload)
    {
        var header = SteamProtoWriter.Build(writer =>
            writer.WriteFixed64(10, JobIdNone));

        var packet = new byte[8 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), msgType | ProtoMask);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), header.Length);
        header.CopyTo(packet.AsSpan(8));
        payload.CopyTo(packet.AsSpan(8 + header.Length));
        return packet;
    }

    private static byte[] ExtractGcMessageBody(uint rawMsgType, byte[] payload)
    {
        var isProto = (rawMsgType & ProtoMask) != 0;
        if (isProto)
        {
            if (payload.Length < 8)
            {
                return Array.Empty<byte>();
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
            if (headerLength < 0 || payload.Length < 8 + headerLength)
            {
                return Array.Empty<byte>();
            }

            return payload[(8 + headerLength)..];
        }

        const int gcBinaryHeaderLength = 18;
        return payload.Length > gcBinaryHeaderLength
            ? payload[gcBinaryHeaderLength..]
            : Array.Empty<byte>();
    }

    private static string GetGcWaiterKey(uint appId, uint msgType)
    {
        return FormattableString.Invariant($"{appId}:{msgType}");
    }

    private static ProtoHeader DecodeProtoHeader(byte[] body)
    {
        var header = new ProtoHeader();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    header.SteamId = reader.ReadFixed64(wireType);
                    break;

                case 2:
                    header.ClientSessionId = (int)reader.ReadVarint(wireType);
                    break;

                case 10:
                    header.JobIdSource = reader.ReadFixed64(wireType);
                    break;

                case 11:
                    header.JobIdTarget = reader.ReadFixed64(wireType);
                    break;

                case 12:
                    header.TargetJobName = reader.ReadString(wireType);
                    break;

                case 13:
                    header.Result = (int)reader.ReadVarint(wireType);
                    break;

                case 14:
                    header.ErrorMessage = reader.ReadString(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return header;
    }

    private static LogonResponse DecodeLogonResponse(byte[] body)
    {
        var response = new LogonResponse(2, 0);
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    response = response with { Result = (int)reader.ReadVarint(wireType) };
                    break;

                case 3:
                    response = response with { HeartbeatSeconds = (int)reader.ReadVarint(wireType) };
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return response;
    }

    private static string? DecodeAccessTokenForAppResponse(byte[] body)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return reader.ReadString(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private static bool DecodeAreFilesInSubscriptionListResponse(byte[] body, ulong publishedFileId)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 1)
            {
                reader.Skip(wireType);
                continue;
            }

            var nested = new SteamProtoReader(reader.ReadLengthDelimited(wireType));
            ulong? id = null;
            var inList = false;

            while (nested.TryReadTag(out var nestedField, out var nestedWireType))
            {
                switch (nestedField)
                {
                    case 1:
                        id = nested.ReadFixed64(nestedWireType);
                        break;

                    case 2:
                        inList = nested.ReadBool(nestedWireType);
                        break;

                    default:
                        nested.Skip(nestedWireType);
                        break;
                }
            }

            if (id == publishedFileId)
            {
                return inList;
            }
        }

        return false;
    }

    private static void EnsureOk(ServiceMethodResponse response, string message)
    {
        if (response.Result != (int)SteamEresult.Ok)
        {
            throw new InvalidOperationException(
                $"{message}：EResult={response.Result} {response.ErrorMessage}");
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.TryGetDouble(out var value)
            ? value
            : double.MaxValue;
    }

    private static string Sha1Hex(string value)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void WriteCString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteByte(Stream stream, byte value)
    {
        stream.WriteByte(value);
    }

    private sealed record ProtoHeader
    {
        public ulong? SteamId { get; set; }
        public int? ClientSessionId { get; set; }
        public ulong? JobIdSource { get; set; }
        public ulong? JobIdTarget { get; set; }
        public string? TargetJobName { get; set; }
        public int? Result { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed record ServiceMethodResponse(
        int Result,
        string? ErrorMessage,
        byte[] Body);

    private sealed record LogonResponse(int Result, int HeartbeatSeconds);

    public sealed record SteamGcClientMessage(
        uint AppId,
        uint MessageType,
        byte[] Payload);
}
