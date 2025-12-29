using NetCoreServer;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FMServer
{
    class GameServer : TcpServer
    {
        private ConcurrentDictionary<Guid, ClientSession> _clients = new();
        private ConcurrentDictionary<string, Channel> _channels = new();

        public GameServer(IPAddress address, int port) : base(address, port) { }

        protected override TcpSession CreateSession()
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
            switch (msg.type)
            {
                case "set_nick":
                    var target = _clients.Values.FirstOrDefault(c => c.Nick == msg.nick);
                    if (target != null && target.Id != sender.Id)
                    {
                        sender.Send(new
                        {
                            type = "set_nick",
                            error = "Nickname already in use."
                        });
                        return;
                    }
                    sender.Nick = msg.nick;
                    sender.Send(new
                    {
                        type = "set_nick",
                        success = true
                    });
                    break;

                case "create_channel":
                    var ch = CreateChannel(sender, msg.channel, msg.hidden, msg.autoclose);
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
                    sender.Send(new
                    {
                        type = "channel_list",
                        channels = list
                    });
                    break;

                case "join_channel":
                    Channel channel;
                    if (!_channels.TryGetValue(msg.channel, out channel))
                    {
                        channel = CreateChannel(sender, msg.channel, msg.hidden ?? false, msg.autoclose ?? true);
                    }
                    JoinChannel(sender, channel);
                    break;

                case "leave_channel":
                    LeaveChannel(sender);
                    break;

                case "channel_text":
                    sender.CurrentChannel?.Broadcast((msg.echo ?? false) ? "" : sender.Nick, new
                    {
                        type = "channel_text",
                        msg.subchannel,
                        client = sender.Nick,
                        msg.text
                    });
                    break;

                case "channel_number":
                    sender.CurrentChannel?.Broadcast((msg.echo ?? false) ? "" : sender.Nick, new
                    {
                        type = "channel_number",
                        msg.subchannel,
                        client = sender.Nick,
                        msg.value
                    });
                    break;

                case "private_text":
                    sender.CurrentChannel?.Send(msg.to, new
                    {
                        type = "private_text",
                        msg.subchannel,
                        client = sender.Nick,
                        msg.text
                    });
                    break;

                case "private_number":
                    sender.CurrentChannel?.Send(msg.to, new
                    {
                        type = "private_number",
                        msg.subchannel,
                        client = sender.Nick,
                        msg.value
                    });
                    break;
            }
        }

        private void JoinChannel(ClientSession client, Channel channel, string password = "")
        {
            if(channel.IsPasswordProtected && channel.Password != password)
            {
                client.Send(new
                {
                    type = "channel_joined",
                    error = "Lobby is password protected."
                });
                return;
            }
            LeaveChannel(client);
            client.CurrentChannel = channel;
            client.Send(new
            {
                type = "channel_joined",
                channel = new {
                    name = channel.Name,
                    owner = channel.Owner.Nick,
                    clients = channel.IsEmpty ? [] : channel.GetMemberNicks()
                }
            });
            channel.Broadcast(client.Nick, new
            {
                type = "channel_user_joined",
                client = client.Nick
            });
            channel.Join(client);
        }

        public void LeaveChannel(ClientSession client)
        {
            var ch = client.CurrentChannel;
            if (ch == null) return;

            ch.Leave(client);
            client.Send(new
            {
                type = "channel_left",
                channelname = ch.Name
            });
            ch.Broadcast(client.Nick, new
            {
                type = "channel_user_left",
                client = client.Nick
            });

            if (ch.AutoClose && ch.IsOwner(client))
            {
                ch.Broadcast(client.Nick, new
                {
                    type = "channel_left",
                    channelname = ch.Name
                });
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
