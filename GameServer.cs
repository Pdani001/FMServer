using NetCoreServer;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace FMServer
{
    class GameServer : WsServer
    {
        private ConcurrentDictionary<Guid, ClientSession> _clients = new();
        private ConcurrentDictionary<string, Channel> _channels = new();

        public GameServer(IPAddress address, int port) : base(address, port) { }

        protected override WsSession CreateSession()
        {
            var s = new ClientSession(this);
            _clients[s.Id] = s;
            return s;
        }

        public void RemoveClient(ClientSession client)
        {
            _clients.TryRemove(client.Id, out _);
        }

        public Channel CreateChannel(ClientSession owner, string name, bool? hidden = false, bool? autoclose = false)
        {
            var ch = new Channel(name, owner, hidden ?? false, autoclose ?? false);
            _channels[name] = ch;
            return ch;
        }

        public void HandleMessage(ClientSession sender, Message msg)
        {
            switch (msg.Type)
            {
                case "set_nick":
                    var target = _clients.Values.FirstOrDefault(c => c.Nick == msg.Nick);
                    if (target != null && target.Id != sender.Id)
                    {
                        sender.SendTextAsync(JsonSerializer.Serialize(new
                        {
                            type = "set_nick",
                            error = "Nickname already in use."
                        }));
                        return;
                    }
                    sender.Nick = msg.Nick;
                    sender.SendTextAsync(JsonSerializer.Serialize(new
                    {
                        type = "set_nick",
                        success = true
                    }));
                    break;

                case "create_channel":
                    var ch = CreateChannel(sender, msg.Channel, msg.Hidden, msg.AutoClose);
                    JoinChannel(sender, ch);
                    break;

                case "list_channels":
                    var list = _channels.Values
                        .Where(c => !c.Hidden)
                        .Select(c => new
                        {
                            name = c.Name,
                            owner = c.Owner.Nick,
                            clients = c.IsEmpty ? [] : c.GetMemberNicks()
                        });
                    sender.SendTextAsync(JsonSerializer.Serialize(new
                    {
                        type = "channel_list",
                        channels = list
                    }));
                    break;

                case "join_channel":
                    Channel channel;
                    if (!_channels.TryGetValue(msg.Channel, out channel))
                    {
                        channel = CreateChannel(sender, msg.Channel, msg.Hidden ?? false, msg.AutoClose ?? true);
                    }
                    JoinChannel(sender, channel);
                    break;

                case "leave_channel":
                    LeaveChannel(sender);
                    break;

                case "channel_text":
                    sender.CurrentChannel?.Broadcast((msg.Echo ?? false) ? "" : sender.Nick, JsonSerializer.Serialize(new
                    {
                        type = "channel_text",
                        subchannel = msg.SubChannel,
                        client = sender.Nick,
                        text = msg.Text
                    }));
                    break;

                case "channel_number":
                    sender.CurrentChannel?.Broadcast((msg.Echo ?? false) ? "" : sender.Nick, JsonSerializer.Serialize(new
                    {
                        type = "channel_number",
                        subchannel = msg.SubChannel,
                        client = sender.Nick,
                        value = msg.Value
                    }));
                    break;

                case "private_text":
                    sender.CurrentChannel?.Send(msg.To, JsonSerializer.Serialize(new
                    {
                        type = "private_text",
                        subchannel = msg.SubChannel,
                        client = sender.Nick,
                        text = msg.Text
                    }));
                    break;

                case "private_number":
                    sender.CurrentChannel?.Send(msg.To, JsonSerializer.Serialize(new
                    {
                        type = "private_number",
                        subchannel = msg.SubChannel,
                        client = sender.Nick,
                        value = msg.Value
                    }));
                    break;
            }
        }

        private void JoinChannel(ClientSession client, Channel channel, string password = "")
        {
            if(channel.IsPasswordProtected && channel.Password != password)
            {
                client.SendTextAsync(JsonSerializer.Serialize(new
                {
                    type = "channel_joined",
                    error = "Lobby is password protected."
                }));
                return;
            }
            LeaveChannel(client);
            client.CurrentChannel = channel;
            client.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "channel_joined",
                channel = new {
                    name = channel.Name,
                    owner = channel.Owner.Nick,
                    clients = channel.IsEmpty ? [] : channel.GetMemberNicks()
                }
            }));
            channel.Broadcast(client.Nick, JsonSerializer.Serialize(new
            {
                type = "channel_user_joined",
                client = client.Nick
            }));
            channel.Join(client);
        }

        public void LeaveChannel(ClientSession client)
        {
            var ch = client.CurrentChannel;
            if (ch == null) return;

            ch.Leave(client);
            client.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "channel_left",
                channelname = ch.Name
            }));
            ch.Broadcast(client.Nick, JsonSerializer.Serialize(new
            {
                type = "channel_user_left",
                client = client.Nick
            }));

            if (ch.AutoClose && ch.IsOwner(client))
            {
                ch.Broadcast(client.Nick, JsonSerializer.Serialize(new
                {
                    type = "channel_left",
                    channelname = ch.Name
                }));
                _channels.TryRemove(ch.Name, out _);
            }

            else if (ch.IsEmpty)
                _channels.TryRemove(ch.Name, out _);

            client.CurrentChannel = null;
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"Server error: {error}");
        }
    }
}
