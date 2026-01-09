using NetCoreServer;
using System;
using System.Text;
using System.Text.Json;

namespace FMServer
{
    public class ClientSession : TcpSession
    {
        ~ClientSession()
        {
            Console.WriteLine($"ClientSession {Id} finalized");
        }
        private readonly GameServer _server;

        public ClientSession(GameServer server) : base(server)
        {
            _server = server;
        }

        public new Guid Id { get; } = Guid.NewGuid();
        public string Nick { get; set; } = "";

        public object Info { get
            {
                return new
                {
                    Id,
                    Nick
                };
            }
        }

        public Channel? CurrentChannel { get; set; }
        
        public bool IsAdmin { get; set; } = false;

        public bool Auth { get; set; }

        public string Nonce { get; } = Guid.NewGuid().ToString();
        public string Session => Id.ToString();

        private readonly MemoryStream _buffer = new();

        public readonly CancellationTokenSource source = new();

        public Character Character { get; set; } = Character.None;

        protected override void OnConnected()
        {
            Task.Delay(5000).WaitAsync(source.Token).ContinueWith(_ =>
            {
                if (!Auth)
                    Disconnect();
                else
                    Send(new { type = "connected" });
            });
        }

        protected override void OnDisconnected()
        {
            _server.LeaveChannel(this);
            _server.RemoveClient(this);
            source.Cancel();
            _buffer.Dispose();
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

                var jsonBytes = Span<byte>.Empty;
                try {
                    jsonBytes = _buffer.GetBuffer().AsSpan(4, length);
                    var msg = JsonSerializer.Deserialize<Message>(jsonBytes, JsonSerializerOptions)!;
                    _server.HandleMessage(this, msg);
                }
                catch (JsonException) 
                {
                    if(!jsonBytes.IsEmpty)
                        Console.WriteLine("Failed to deserialize message: " + Encoding.UTF8.GetString(jsonBytes));
                    else
                        Console.WriteLine("Invalid message received!");
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
