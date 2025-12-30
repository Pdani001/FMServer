using NetCoreServer;
using System;
using System.Text;
using System.Text.Json;

namespace FMServer
{
    public class ClientSession(TcpServer server) : TcpSession(server)
    {
        public new Guid Id { get; } = Guid.NewGuid();
        public string Nick { get; set; } = "Guest";
        public Channel? CurrentChannel { get; set; }
        
        public bool IsDev { get; set; } = false;

        public bool Auth => !string.IsNullOrEmpty(Session);

        public string Nonce { get; } = Guid.NewGuid().ToString();
        public string? Session { get; set; }

        private readonly MemoryStream _buffer = new();

        public readonly CancellationTokenSource source = new();

        private new GameServer Server => (GameServer)server;

        protected override void OnConnected()
        {
            Send(new { type = "challenge", text = Nonce });
            CancellationToken token = source.Token;
            Task.Delay(5000).WaitAsync(token).ContinueWith(_ =>
            {
                if (!Auth)
                    Disconnect();
                else
                    Send(new { type = "connected" });
            });
        }

        protected override void OnDisconnected()
        {
            Server.LeaveChannel(this);
            Server.RemoveClient(this);
        }

        private static JsonSerializerOptions JsonSerializerOptions => new()
        {
            PropertyNameCaseInsensitive = true
        };

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            _buffer.Write(buffer, (int)offset, (int)size);

            while (true)
            {
                if (_buffer.Length < 4)
                    return;

                _buffer.Position = 0;
                int length = BitConverter.ToInt32(_buffer.GetBuffer(), 0);

                if (_buffer.Length < length + 4)
                    return;

                var jsonBytes = _buffer.GetBuffer().AsSpan(4, length);
                try {
                    var msg = JsonSerializer.Deserialize<Message>(jsonBytes, JsonSerializerOptions)!;
                    Server.HandleMessage(this, msg);
                }
                catch (JsonException) 
                {
                    Console.WriteLine("Failed to deserialize message: " + Encoding.UTF8.GetString(jsonBytes));
                }

                var remaining = _buffer.Length - (length + 4);
                var temp = new byte[remaining];
                Array.Copy(_buffer.GetBuffer(), length + 4, temp, 0, remaining);

                _buffer.SetLength(0);
                _buffer.Write(temp, 0, temp.Length);
            }
        }

        public void Send(object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var data = Encoding.UTF8.GetBytes(json);

            var packet = new byte[data.Length + 4];
            BitConverter.GetBytes(data.Length).CopyTo(packet, 0);
            data.CopyTo(packet, 4);

            SendAsync(packet);
        }
    }
}
