using NetCoreServer;
using System.Text.Json;

namespace FMServer
{
    public class ClientSession(WsServer server) : WsSession(server)
    {
        public new Guid Id { get; } = Guid.NewGuid();
        public string Nick { get; set; } = "Guest";
        public Channel? CurrentChannel { get; set; }

        private new GameServer Server => (GameServer)server;

        public override void OnWsConnected(HttpRequest request)
        {
            SendTextAsync("{\"type\":\"connected\"}");
            Console.WriteLine($"Client connected: {Id}");
        }

        public override void OnWsDisconnected()
        {
            Server.LeaveChannel(this);
            Server.RemoveClient(this);
        }

        public override void OnWsReceived(byte[] buffer, long offset, long size)
        {
            var json = System.Text.Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            var msg = JsonSerializer.Deserialize<Message>(json);

            Server.HandleMessage(this, msg);
        }
    }
}
