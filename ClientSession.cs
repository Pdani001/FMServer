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

        private readonly MemoryStream _buffer = new();

        private new GameServer Server => (GameServer)server;

        protected override void OnConnected()
        {
            Send(new { type = "connected" });
        }

        protected override void OnDisconnected()
        {
            Server.LeaveChannel(this);
            Server.RemoveClient(this);
        }

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
                var msg = JsonSerializer.Deserialize<Message>(jsonBytes);

                Server.HandleMessage(this, msg);

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
