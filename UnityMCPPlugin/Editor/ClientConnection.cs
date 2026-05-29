using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityMCP.Editor
{
    // One connected MCP client, wrapping its TCP socket with a hand-rolled WebSocket
    // implementation (RFC 6455). Unity's Mono runtime does not reliably support HttpListener's
    // WebSocket upgrade, so the handshake and framing are done by hand over a raw socket here.
    //
    // This type owns the WebSocket wire protocol end-to-end - AcceptAsync performs the upgrade
    // handshake, ReceiveMessageAsync/SendTextAsync move text messages, and all the framing
    // (masking, fragmentation, ping/pong, close) stays internal. UnityMCPConnection deals only in
    // whole string messages and never touches frames.
    internal sealed class ClientConnection
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpClient tcp;
        private readonly NetworkStream stream;

        // A WebSocket forbids overlapping writes, and a request's response can race a log
        // broadcast on the same socket; this serializes every write to the client.
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        // Diagnostics (the remote endpoint is captured now because it's unavailable after close).
        public string RemoteEndPoint { get; }
        public DateTime ConnectedAtUtc { get; }

        private ClientConnection(TcpClient tcp, NetworkStream stream)
        {
            this.tcp = tcp;
            this.stream = stream;
            try { RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString(); } catch { /* ignore */ }
            if (string.IsNullOrEmpty(RemoteEndPoint)) RemoteEndPoint = "unknown";
            ConnectedAtUtc = DateTime.UtcNow;
        }

        // Performs the WebSocket upgrade handshake. Returns a ready connection, or null if the
        // request wasn't a valid WebSocket upgrade (the caller closes the socket in that case).
        public static async Task<ClientConnection> AcceptAsync(TcpClient tcp, CancellationToken token)
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            if (!await PerformHandshake(stream, token).ConfigureAwait(false))
            {
                return null;
            }
            return new ClientConnection(tcp, stream);
        }

        public void Close()
        {
            try { tcp.Close(); } catch { }
        }

        public Task SendTextAsync(string message, CancellationToken token)
        {
            return SendFrameAsync(0x1, Encoding.UTF8.GetBytes(message), token);
        }

        // Returns the next complete text message, or null when the connection closes. Handles
        // fragmentation, client masking, ping/pong, and close frames.
        public async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            var payload = new List<byte>();

            while (true)
            {
                var header = await ReadExactlyAsync(2, token).ConfigureAwait(false);
                if (header == null) return null;

                bool fin = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long len = header[1] & 0x7F;

                if (len == 126)
                {
                    var ext = await ReadExactlyAsync(2, token).ConfigureAwait(false);
                    if (ext == null) return null;
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    var ext = await ReadExactlyAsync(8, token).ConfigureAwait(false);
                    if (ext == null) return null;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }

                byte[] mask = null;
                if (masked)
                {
                    mask = await ReadExactlyAsync(4, token).ConfigureAwait(false);
                    if (mask == null) return null;
                }

                var data = len > 0
                    ? await ReadExactlyAsync((int)len, token).ConfigureAwait(false)
                    : new byte[0];
                if (data == null) return null;

                if (masked)
                {
                    for (int i = 0; i < data.Length; i++) data[i] ^= mask[i % 4];
                }

                switch (opcode)
                {
                    case 0x8: // close
                        try { await SendFrameAsync(0x8, new byte[0], token).ConfigureAwait(false); } catch { }
                        return null;
                    case 0x9: // ping -> pong with same payload
                        await SendFrameAsync(0xA, data, token).ConfigureAwait(false);
                        continue;
                    case 0xA: // pong - ignore
                        continue;
                    default: // 0x0 continuation, 0x1 text, 0x2 binary
                        payload.AddRange(data);
                        break;
                }

                if (fin)
                {
                    return Encoding.UTF8.GetString(payload.ToArray());
                }
                // otherwise keep reading continuation frames
            }
        }

        private async Task SendFrameAsync(int opcode, byte[] payload, CancellationToken token)
        {
            var frame = BuildFrame(opcode, payload);
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        // Server-to-client frames are never masked.
        private static byte[] BuildFrame(int opcode, byte[] payload)
        {
            var header = new List<byte> { (byte)(0x80 | (opcode & 0x0F)) };
            int len = payload.Length;
            if (len <= 125)
            {
                header.Add((byte)len);
            }
            else if (len <= 65535)
            {
                header.Add(126);
                header.Add((byte)((len >> 8) & 0xFF));
                header.Add((byte)(len & 0xFF));
            }
            else
            {
                header.Add(127);
                long wide = len; // promote so shifts past 31 bits are correct
                for (int i = 7; i >= 0; i--) header.Add((byte)((wide >> (8 * i)) & 0xFF));
            }

            var frame = new byte[header.Count + payload.Length];
            for (int i = 0; i < header.Count; i++) frame[i] = header[i];
            Buffer.BlockCopy(payload, 0, frame, header.Count, payload.Length);
            return frame;
        }

        private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken token)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = await stream.ReadAsync(buf, off, count - off, token).ConfigureAwait(false);
                if (n == 0) return null; // closed
                off += n;
            }
            return buf;
        }

        // --- WebSocket upgrade handshake (RFC 6455 server side) ---

        private static async Task<bool> PerformHandshake(NetworkStream stream, CancellationToken token)
        {
            var headerText = await ReadHttpHeadersAsync(stream, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerText)) return false;

            string key = null;
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(key)) return false;

            string accept;
            using (var sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(
                    sha1.ComputeHash(Encoding.UTF8.GetBytes(key + WebSocketGuid)));
            }

            var response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            return true;
        }

        // Read HTTP request headers one byte at a time, stopping exactly at the blank line so we
        // don't consume any bytes of the first WebSocket frame that follows.
        private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken token)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (sb.Length < 16384)
            {
                int n = await stream.ReadAsync(buf, 0, 1, token).ConfigureAwait(false);
                if (n == 0) return null; // closed
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 4] == '\r')
                {
                    return sb.ToString();
                }
            }
            return null; // headers too large
        }
    }
}
