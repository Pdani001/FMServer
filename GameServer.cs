using NetCoreServer;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FMServer
{
    class GameServer(IPAddress address, int port) : TcpServer(address, port)
    {
        static GameServer()
        {
            try
            {
                TICK_RATE = int.Parse(Environment.GetEnvironmentVariable("TICK_RATE") ?? TICK_RATE.ToString());
            }
            catch { }
            TICK_INTERVAL_MS = 1000 / TICK_RATE;
        }
        public static readonly int TICK_RATE = 10;
        public static readonly int TICK_INTERVAL_MS = 1000/TICK_RATE;

        private ConcurrentDictionary<Guid, ClientSession> _clients = new();
        private ConcurrentDictionary<string, Channel> _channels = new();

        public string ServerSecret { get; set; } = "";

        private readonly string ClientSecret = "[9edp!J3qWd4)XWtW#sa@s@>PJaXEW]Ns0FzYi5{WEA4pfCjgbeEU3+exR)+ww2(";

        private Regex nameRegex = new("^[a-zA-Z0-9_]{3,24}$");

        public string AdminName { get; set; }

        protected override TcpSession CreateSession()
        {
            var s = new ClientSession(this);
            _clients[s.Id] = s;
            return s;
        }

        protected override void OnStarted()
        {
            if(ServerSecret == "")
            {
                ServerSecret = StringExt.RandomString(32);
                Console.WriteLine($"Server secret: {ServerSecret}");
            }
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
            var senderChannel = sender.CurrentChannel;

            if (senderChannel != null && senderChannel.State != ChannelState.Lobby)
            {
                if(senderChannel.State == ChannelState.Starting)
                {
                    if(msg.Type == "ready_check")
                    {
                        senderChannel.GameState.SetPlayerReady(sender.Nick);
                        if(senderChannel.GameState.ReadyPlayerCount >= senderChannel.GetMemberNicks().Length)
                        {
                            senderChannel.Start();
                        }
                    }
                    return;
                }
                // Gameplay input
                if (!senderChannel.ValidateClientTick(msg.Tick))
                    return;

                senderChannel.InputQueue.Enqueue(new QueuedInput {
                    Client = sender,
                    Message = msg,
                    ClientTick = msg.Tick.Value,
                    ReceivedAtTick = senderChannel.CurrentTick
                });
                return;
            }
            switch (msg.Type)
            {
                case "auth":
                    if(sender.Auth)
                        return;
                    bool valid = false;
                    using (SHA256 sha256Hash = SHA256.Create())
                    {
                        var source = sender.Nonce + ClientSecret;
                        valid = VerifyHash(sha256Hash, source, msg.Text ?? "");
                    }
                    if (valid)
                    {
                        sender.Session = Guid.NewGuid().ToString();
                        sender.Send(new
                        {
                            type = "auth",
                            text = sender.Session
                        });
                    }
                    else
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid secret."
                        });
                    }
                    sender.source.Cancel();
                    break;

                case "set_nick":
                    if(!sender.Auth || sender.Session != msg.Session || !sender.NoNick)
                        return;
                    var target = _clients.Values.FirstOrDefault(c => c.Nick == msg.Nick);
                    if (target != null && target.Id != sender.Id)
                    {
                        sender.Send(new
                        {
                            type = "set_nick",
                            error = "Nickname already in use."
                        });
                        return;
                    }
                    if (!nameRegex.IsMatch(msg.Nick))
                    {
                        sender.Send(new
                        {
                            type = "set_nick",
                            error = "Invalid nickname. Use 3-24 alphanumeric characters or underscores."
                        });
                        return;
                    }
                    if(msg.Nick.Equals(AdminName, StringComparison.CurrentCultureIgnoreCase) && !sender.IsAdmin || msg.Nick.Equals("fmserver", StringComparison.CurrentCultureIgnoreCase))
                    {
                        sender.Send(new
                        {
                            type = "set_nick",
                            error = "This nickname is reserved."
                        });
                        return;
                    }
                    sender.Nick = msg.Nick;
                    sender.Send(new
                    {
                        type = "set_nick",
                        success = true
                    });
                    break;

                case "create_channel":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick)
                        return;
                    if (!nameRegex.IsMatch(msg.Channel))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid name. Use 3-24 alphanumeric characters or underscores."
                        });
                        return;
                    }
                    var ch = CreateChannel(sender, msg.Channel, msg.Hidden, msg.AutoClose);
                    JoinChannel(sender, ch);
                    break;

                case "list_channels":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick)
                        return;
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
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick)
                        return;
                    //Channel channel;
                    if (!_channels.TryGetValue(msg.Channel, out var channel))
                    {
                        if (!nameRegex.IsMatch(msg.Channel))
                        {
                            sender.Send(new
                            {
                                type = "set_nick",
                                error = "Invalid name. Use 3-24 alphanumeric characters or underscores."
                            });
                            return;
                        }
                        channel = CreateChannel(sender, msg.Channel, msg.Hidden ?? false, msg.AutoClose ?? true);
                    }
                    JoinChannel(sender, channel);
                    break;

                case "leave_channel":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick)
                        return;
                    LeaveChannel(sender);
                    break;

                case "chat":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick)
                        return;
                    senderChannel?.Broadcast(new
                    {
                        type = "chat",
                        client = sender.Nick,
                        msg.Text,
                        sender.IsAdmin
                    });
                    break;

                case "select":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick || senderChannel == null)
                        return;
                    senderChannel.GameState.SetPlayerCharacter(sender.Nick, (Character)(msg.Value ?? 5));
                    break;

                case "ready":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick || senderChannel == null)
                        return;
                    if(senderChannel.State == ChannelState.Starting)
                        senderChannel.Abort();
                    senderChannel.GameState.SetPlayerReady(sender.Nick, msg.Value == 1);
                    senderChannel.Broadcast(new
                    {
                        type = "ready",
                        client = sender.Nick,
                        ready = msg.Value == 1
                    });
                    break;

                case "game_start":
                    if (!sender.Auth || sender.Session != msg.Session || sender.NoNick || senderChannel == null)
                        return;
                    if (senderChannel.IsOwner(sender) && senderChannel.GameState.ReadyPlayerCount >= senderChannel.GetMemberNicks().Length)
                    {
                        senderChannel.Countdown();
                    }
                    break;

                case "server_secret":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    if (ServerSecret != "" && msg.Text == ServerSecret)
                    {
                        Console.WriteLine($"Client {sender.Id} authenticated with server secret.");
                        sender.IsAdmin = true;
                        sender.Send(new
                        {
                            type = "server_secret",
                            success = true
                        });
                    }
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
            channel.Broadcast(new
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
            ch.Broadcast(new
            {
                type = "channel_user_left",
                client = client.Nick
            });

            if (ch.AutoClose && ch.IsOwner(client))
            {
                ch.Broadcast(new
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

        private static string GetHash(HashAlgorithm hashAlgorithm, string input)
        {

            // Convert the input string to a byte array and compute the hash.
            byte[] data = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(input));

            // Create a new Stringbuilder to collect the bytes
            // and create a string.
            var sBuilder = new StringBuilder();

            // Loop through each byte of the hashed data
            // and format each one as a hexadecimal string.
            for (int i = 0; i < data.Length; i++)
            {
                sBuilder.Append(data[i].ToString("x2"));
            }

            // Return the hexadecimal string.
            return sBuilder.ToString();
        }

        // Verify a hash against a string.
        private static bool VerifyHash(HashAlgorithm hashAlgorithm, string input, string hash)
        {
            // Hash the input.
            var hashOfInput = GetHash(hashAlgorithm, input);

            // Create a StringComparer an compare the hashes.
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;

            return comparer.Compare(hashOfInput, hash) == 0;
        }
    }
}
