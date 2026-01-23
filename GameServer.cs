using NetCoreServer;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FMServer
{
    public class GameServer(IPAddress address, int port) : TcpServer(address, port)
    {
        static GameServer()
        {
            try
            {
                TICK_RATE = Math.Clamp(int.Parse(Environment.GetEnvironmentVariable("TICK_RATE") ?? TICK_RATE.ToString()), 10, 200);
            }
            catch { }
            TICK_INTERVAL_MS = 1000 / TICK_RATE;
        }
        public static readonly int TICK_RATE = 10;
        public static readonly int TICK_INTERVAL_MS = 1000/TICK_RATE;
        public const int PROTOCOL_VERSION = 4;

        private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
        private readonly ConcurrentDictionary<string, Channel> _channels = new();

        public bool ChannelInGame => _channels.Any(kvp=>kvp.Value.State!=ChannelState.Lobby);

        public string[] ChannelList => [.. _channels.Select(kvp => $"{kvp.Key} ({kvp.Value.GetMembers().Count}): {kvp.Value.State}")];

        public string ServerSecret { get; set; } = "";

        private readonly string ClientHash = "[9edp!J3qWd4)XWtW#sa@s@>PJaXEW]Ns0FzYi5{WEA4pfCjgbeEU3+exR)+ww2(";

        private readonly Regex nameRegex = new("^[a-zA-Z0-9_]{3,24}$");

        public string AdminName { get; set; } = "";

        public static readonly Random RNG = new(Guid.NewGuid().GetHashCode());

        protected override TcpSession CreateSession()
        {
            var s = new ClientSession(this);
            _clients[s.Id] = s;
            return s;
        }

        protected override void OnStarted()
        {
            Console.WriteLine($"[{DateTime.Now}] Running protocol version "+PROTOCOL_VERSION);
            if(ServerSecret == "")
            {
                ServerSecret = StringExt.RandomString(32);
                Console.WriteLine($"[{DateTime.Now}] Server secret: {ServerSecret}");
            }
        }

        public void RemoveClient(ClientSession client)
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
        }

        public Channel CreateChannel(ClientSession owner, string name, bool? hidden = false)
        {
            var ch = new Channel(name, owner, hidden ?? false);
            _channels[name] = ch;
            return ch;
        }

        public void HandleMessage(ClientSession sender, Message msg)
        {
            if (msg.Type == "ping")
            {
                sender.IsAlive = true;
                sender.LastAlive = 0;
                return;
            }
            var senderChannel = sender.CurrentChannel;

            if (senderChannel != null && senderChannel.State != ChannelState.Lobby)
            {
                if(senderChannel.State == ChannelState.Starting)
                {
                    if(msg.Type == "ready")
                    {
                        senderChannel.GameState.SetPlayerReady(sender.Id);
                        if(senderChannel.GameState.ReadyPlayerCount >= senderChannel.GetMembers().Count)
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
                case "hello":
                    if (sender.Auth)
                        return;
                    if ((msg.Value ?? 0) < PROTOCOL_VERSION)
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid protocol version! (Are you using an older client?)"
                        });
                        sender.source.Cancel();
                        return;
                    }
                    sender.Send(new { type = "challenge", text = sender.Nonce });
                    break;

                case "auth":
                    if (sender.Auth)
                        return;
                    bool valid = false;
                    using (SHA256 sha256Hash = SHA256.Create())
                    {
                        var source = sender.Nonce + ClientHash;
                        valid = VerifyHash(sha256Hash, source, msg.Text ?? "");
                    }
                    if (valid)
                    {
                        sender.Auth = true;
                        sender.Send(new
                        {
                            type = "auth",
                            client = sender.Info
                        });
                    }
                    else
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid response hash. (Are you using a modified client?)"
                        });
                    }
                    sender.source.Cancel();
                    break;

                case "create_channel":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    if (msg.Nick == null || !nameRegex.IsMatch(msg.Nick))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid nickname. Use 3-24 alphanumeric characters or underscores."
                        });
                        return;
                    }
                    if (msg.Nick.Equals(AdminName, StringComparison.CurrentCultureIgnoreCase) && !sender.IsAdmin || msg.Nick.Equals("fmserver", StringComparison.CurrentCultureIgnoreCase))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "This nickname is reserved."
                        });
                        return;
                    }
                    if (msg.Channel == null || !nameRegex.IsMatch(msg.Channel))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid lobby name. Use 3-24 alphanumeric characters or underscores."
                        });
                        return;
                    }
                    if (_channels.ContainsKey(msg.Channel))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Lobby name already in use."
                        });
                        return;
                    }
                    sender.Nick = msg.Nick;
                    var ch = CreateChannel(sender, msg.Channel, msg.Hidden);
                    JoinChannel(sender, ch);
                    break;

                case "list_channels":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    var list = _channels.Values
                        .Where(c => !c.Hidden && c.State == ChannelState.Lobby)
                        .Select(c => new
                        {
                            name = c.Name,
                            owner = c.Owner.Id,
                            clients = c.IsEmpty ? [] : c.GetMembers(),
                            maxplayers = c.MaxPlayers,
                            password = c.IsPasswordProtected
                        }).ToArray();
                    sender.Send(new
                    {
                        type = "channel_list",
                        channels = list
                    });
                    break;

                case "join_channel":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    if (msg.Nick == null || !nameRegex.IsMatch(msg.Nick))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "Invalid nickname. Use 3-24 alphanumeric characters or underscores."
                        });
                        return;
                    }
                    if (msg.Nick.Equals(AdminName, StringComparison.CurrentCultureIgnoreCase) && !sender.IsAdmin || msg.Nick.Equals("fmserver", StringComparison.CurrentCultureIgnoreCase))
                    {
                        sender.Send(new
                        {
                            type = "error",
                            error = "This nickname is reserved."
                        });
                        return;
                    }
                    if (senderChannel != null)
                    {
                        // Regular players should never be able to see this
                        sender.Send(new
                        {
                            type = "error",
                            error = "You are already in a lobby!"
                        });
                        return;
                    }
                    if (!_channels.TryGetValue(msg.Channel ?? "", out var channel))
                    {
                        if (msg.Channel == null || !nameRegex.IsMatch(msg.Channel))
                        {
                            sender.Send(new
                            {
                                type = "error",
                                error = "Invalid lobby name. Use 3-24 alphanumeric characters or underscores."
                            });
                            return;
                        }
                        channel = CreateChannel(sender, msg.Channel, msg.Hidden ?? false);
                    }
                    sender.Nick = msg.Nick;
                    JoinChannel(sender, channel, msg.Text ?? "");
                    break;

                case "leave_channel":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    LeaveChannel(sender);
                    break;

                case "chat":
                    if (!sender.Auth || sender.Session != msg.Session || string.IsNullOrEmpty(msg.Text))
                        return;
                    if (msg.Text.Length >= 256)
                        msg.Text = msg.Text[..256];
                    if (msg.Text.StartsWith('/'))
                    {
                        var args = msg.Text[1..].Split(' ');
                        senderChannel?.HandleCommand(this, sender, args[0], args[1..]);
                        return;
                    }
                    senderChannel?.Broadcast(new
                    {
                        type = "chat",
                        client = sender.Info,
                        msg.Text,
                        sender.IsAdmin
                    });
                    break;

                case "select":
                    if (!sender.Auth || sender.Session != msg.Session || senderChannel == null)
                    {
                        return;
                    }
                    if (senderChannel.GameState.IsPlayerReady(sender.Id) || msg.Value == null || !Enum.IsDefined(typeof(Character), msg.Value))
                        return;
                    var character = (Character)msg.Value;
                    if (character != Character.None && senderChannel.GameState.IsCharacterPlaying(character) || character == senderChannel.GameState.GetPlayerCharacter(sender.Id))
                        return;
                    if(senderChannel.GameState.SetPlayerCharacter(sender.Id, character))
                    {
                        senderChannel.Broadcast(new
                        {
                            type = "select",
                            client = sender.Info,
                            value = character
                        });
                        return;
                    }
                    break;

                case "ready":
                    if (!sender.Auth || sender.Session != msg.Session || senderChannel == null)
                        return;
                    if(senderChannel.GameState.SetPlayerReady(sender.Id, msg.Value == 1))
                    {
                        if(senderChannel.IsCountdown)
                            senderChannel.Abort();
                        character = senderChannel.GameState.GetPlayerCharacter(sender.Id);
                        senderChannel.Broadcast(new
                        {
                            type = "ready",
                            value = character,
                            ready = msg.Value == 1
                        });
                        if (senderChannel.GetMembers().Count >= 2 && senderChannel.GameState.ReadyPlayerCount == senderChannel.GetMembers().Count && senderChannel.State == ChannelState.Lobby)
                        {
                            senderChannel.Countdown();
                        }
                    }
                    break;

                case "server_secret":
                    if (!sender.Auth || sender.Session != msg.Session)
                        return;
                    if (ServerSecret != "" && msg.Text == ServerSecret)
                    {
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
            if(channel.State != ChannelState.Lobby)
            {
                client.Send(new
                {
                    type = "channel_joined",
                    error = "This lobby is already in game"
                });
                return;
            }
            if(channel.GetMembers().Count == channel.MaxPlayers)
            {
                client.Send(new
                {
                    type = "channel_joined",
                    error = "This lobby is full"
                });
                return;
            }
            LeaveChannel(client);
            client.CurrentChannel = channel;
            channel.Join(client);
            client.Send(new
            {
                type = "channel_joined",
                channel = new
                {
                    name = channel.Name,
                    owner = channel.Owner.Id,
                    clients = channel.IsEmpty ? [] : channel.GetMembers(),
                    maxplayers = channel.MaxPlayers,
                    password = channel.IsPasswordProtected
                },
            });
            channel.Broadcast(new
            {
                type = "channel_user_joined",
                selected = channel.GameState.GetSelected(),
                client = client.Info
            });
        }

        public void LeaveChannel(ClientSession client, string reason = "")
        {
            var ch = client.CurrentChannel;
            if (ch == null) return;
            client.CurrentChannel = null;
            ch.Leave(client);
            client.Send(new
            {
                type = "channel_left",
                channelname = ch.Name
            });
            ch.Broadcast(new
            {
                type = "channel_user_left",
                client = client.Info,
                text = reason
            });

            if (ch.IsEmpty)
            {
                ch.Dispose();
                _channels.TryRemove(ch.Name, out _);
            }
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"Server error: {error}");
        }

        private static string GetHash(HashAlgorithm hashAlgorithm, string input)
        {
            byte[] data = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                sBuilder.Append(data[i].ToString("x2"));
            }
            return sBuilder.ToString();
        }

        private static bool VerifyHash(HashAlgorithm hashAlgorithm, string input, string hash)
        {
            var hashOfInput = GetHash(hashAlgorithm, input);
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;
            return comparer.Compare(hashOfInput, hash) == 0;
        }
    }
}
