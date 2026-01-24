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
        private GameServer _server;

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

        public bool Auth { get; set; } = false;

        public string Nonce { get; } = StringExt.RandomString(24);
        public string Session => Id.ToString();

        private readonly MemoryStream _buffer = new();

        public readonly CancellationTokenSource source = new();
        public readonly CancellationTokenSource ping = new();
        public bool IsAlive { get; set; } = true;
        public int LastAlive { get; set; } = 0;

        public Character Character { get; set; } = Character.None;

        protected override void OnConnected()
        {
            Task.Delay(5000).WaitAsync(source.Token).ContinueWith(FinishConnect);
        }

        private void FinishConnect(Task task)
        {
            if (!Auth)
                Disconnect();
            else
            {
                Send(new { type = "connected" });
                Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    while (!ping.IsCancellationRequested)
                    {
                        await timer.WaitForNextTickAsync(ping.Token);
                        if (ping.IsCancellationRequested)
                            break;
                        if (LastAlive >= 5)
                        {
                            ping.Cancel();
                            break;
                        }
                        if (!IsAlive)
                            LastAlive++;
                        IsAlive = false;
                        Send(new { type = "ping" });
                    }
                    if (ping.IsCancellationRequested)
                    {
                        Disconnect();
                    }
                });
            }
            task.Dispose();
        }

        protected override void OnDisconnected()
        {
            var server = _server;
            if (server == null)
                return;
            server.LeaveChannel(this, ping.IsCancellationRequested ? "Timed out" : "");
            server.RemoveClient(this);
            source.Cancel();
            ping.Cancel();
            _buffer.Dispose();
            _server = null!;
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

                if (length <= 0)
                {
                    Console.WriteLine($"Invalid packet length {length} from {Id}");
                    Disconnect();
                    return;
                }

                if (_buffer.Length < length + 4)
                    return;

                var jsonBytes = _buffer.GetBuffer().AsSpan(4, length);
                try {
                    var msg = JsonSerializer.Deserialize<Message>(jsonBytes, JsonSerializerOptions) ?? throw new JsonException("Null message");
                    _server.HandleMessage(this, msg);
                }
                catch (Exception ex) 
                {
                    Console.WriteLine($"Invalid message from {Id}: {ex.Message}");
                    Disconnect();
                    return;
                }

                var remaining = (int)_buffer.Length - (length + 4);
                if (remaining > 0)
                {
                    System.Buffer.BlockCopy(
                        _buffer.GetBuffer(),
                        length + 4,
                        _buffer.GetBuffer(),
                        0,
                        remaining
                    );
                }

                _buffer.SetLength(remaining);
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
