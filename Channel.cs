using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FMServer
{
    public class Channel
    {
        public string Name { get; }
        public ClientSession Owner { get; private set; }
        public ChannelState State = ChannelState.Lobby;
        public long CurrentTick { get; private set; } = 0;
        public bool Running;
        public bool Hidden { get; }
        public bool AutoClose { get; }

        public string Password { get; set; } = "";

        private ConcurrentDictionary<Guid, ClientSession> _members = new();
        public readonly ConcurrentQueue<QueuedInput> InputQueue = new();
        private Thread? TickThread;

        public GameState GameState { get; private set; } = new();

        public Channel(string name, ClientSession owner, bool hidden, bool autoClose)
        {
            Name = name;
            Owner = owner;
            Hidden = hidden;
            AutoClose = autoClose;
            _members[owner.Id] = owner;
        }

        public void Abort()
        {
            if (State == ChannelState.Starting)
            {
                countdown?.Dispose();
                countdown = null;
                State = ChannelState.Lobby;
                Broadcast(new
                {
                    type = "game_starting",
                    countdown = -1
                });
            }
        }

        private Task? countdown;
        public void Countdown()
        {
            if (State != ChannelState.Lobby)
                return;
            State = ChannelState.Starting;
            countdown = Task.Run(() =>
            {
                int count = 5;
                while(count > 0)
                {
                    Broadcast(new
                    {
                        type = "game_starting",
                        countdown = count
                    });
                    Thread.Sleep(1000);
                    count--;
                }
                Start();
            });
        }

        public void Start()
        {
            if(State != ChannelState.Starting)
                return;
            Broadcast(new
            {
                type = "game_start"
            });
            State = ChannelState.InGame;
            CurrentTick = 0;
            Running = true;

            TickThread = new Thread(() => RunChannelTickLoop());
            TickThread.Start();
        }

        private void RunChannelTickLoop()
        {
            var stopwatch = Stopwatch.StartNew();
            long nextTickMs = 0;

            while (Running)
            {
                long elapsed = stopwatch.ElapsedMilliseconds;

                if (elapsed >= nextTickMs)
                {
                    Tick();
                    nextTickMs += GameServer.TICK_INTERVAL_MS;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }

        private void Tick()
        {
            CurrentTick++;

            ProcessInputs();
            UpdateLogic();
            BroadcastSnapshot();
        }

        private long lastNightTimeUpdateTick = 0;
        private long lastPowerUpdateTick = 0;

        private void UpdateLogic()
        {
            if(CurrentTick - lastNightTimeUpdateTick >= GameServer.TICK_RATE * 86)
            {
                if(GameState.NightTime == 12)
                {
                    GameState.NightTime = 1;
                }
                else
                {
                    GameState.NightTime += 1;
                }
                lastNightTimeUpdateTick = CurrentTick;
            }
            if (GameState.Power > 0 && CurrentTick - lastPowerUpdateTick >= GameServer.TICK_RATE)
            {
                bool[] systems =
                [
                    GameState.RightDoor,
                    GameState.LeftDoor,
                    GameState.RightLight ||
                    GameState.LeftLight ||
                    GameState.CameraActive
                ];
                int usage = 1 + systems.Count(c => c == true);
                GameState.Power -= usage;
                if(GameState.Power < 0)
                {
                    GameState.Power = 0;
                }
                lastPowerUpdateTick = CurrentTick;
            }
            foreach(var character in GameState.GetPlayingRobots())
            {
                int timer = GameState.GetCharacterMoveTimer(character);
                if(timer > 0)
                {
                    timer -= 1;
                    GameState.SetCharacterMoveTimer(character, timer);
                    _members.Values.FirstOrDefault(c => GameState.GetPlayerCharacter(c.Nick) == character)?.Send(new
                    {
                        type = "move_timer",
                        value = timer
                    });
                }
            }
        }

        void ApplyValidatedInput(QueuedInput input)
        {
            Message msg = input.Message;
            switch (msg.Type)
            {
                case "door":
                    if (GameState.GetPlayerCharacter(input.Client.Nick) != Character.Guard || GameState.Power <= 0 || GameState.CameraActive)
                        return;
                    if(msg.LeftSide == true)
                    {
                        if(!GameState.BlockLeft)
                            GameState.LeftDoor = msg.Value == 1;
                    }
                    else
                    {
                        if(!GameState.BlockRight)
                            GameState.RightDoor = msg.Value == 1;
                    }
                    break;
                case "light":
                    if (GameState.GetPlayerCharacter(input.Client.Nick) != Character.Guard || GameState.Power <= 0 || GameState.CameraActive)
                        return;
                    if (msg.LeftSide == true)
                    {
                        if (!GameState.BlockLeft)
                        {
                            GameState.LeftLight = msg.Value == 1;
                            GameState.RightLight = false;
                        }
                    }
                    else
                    {
                        if (!GameState.BlockRight) {
                            GameState.LeftLight = false;
                            GameState.RightLight = msg.Value == 1;
                        }
                    }
                    break;
                case "camera":
                    if (GameState.GetPlayerCharacter(input.Client.Nick) != Character.Guard || GameState.Power <= 0)
                        return;
                    GameState.CameraActive = msg.Value == 1;
                    break;
                case "move":
                    var movechar = GameState.GetPlayerCharacter(input.Client.Nick);
                    var movetime = GameState.GetCharacterMoveTimer(movechar);
                    if (movechar == Character.Guard || movetime > 0 || msg.Value == null)
                        return;
                    int target = (int)msg.Value;
                    if (!GameState.IsValidPosition(movechar, target))
                    {
                        return;
                    }
                    switch (movechar)
                    {
                        case Character.Freddy:
                            switch (target)
                            {
                                case 6:
                                case 7:
                                    if (GameState.GetCharacterPosition(Character.Chica) == target)
                                        return;
                                    break;
                                case 1:
                                    if (GameState.GetCharacterPosition(Character.Bonnie) == 0 || GameState.GetCharacterPosition(Character.Chica) == 0)
                                        return;
                                    break;
                            }
                            break;
                        case Character.Chica:
                            switch (target)
                            {
                                case 6:
                                case 7:
                                    if (GameState.GetCharacterPosition(Character.Freddy) == target)
                                        return;
                                    break;
                            }
                            break;
                        case Character.Bonnie:
                            switch (target)
                            {
                                case 3:
                                    if (GameState.GetCharacterPosition(Character.Foxy) >= 3)
                                        return;
                                    break;
                            }
                            break;
                        case Character.Foxy:
                            switch (target)
                            {
                                case 3:
                                    if (GameState.GetCharacterPosition(Character.Bonnie) == 3)
                                        return;
                                    break;
                            }
                            break;
                    }
                    GameState.SetCharacterPosition(movechar, target);
                    Broadcast(new
                    {
                        type = "move",
                        character = movechar,
                        value = target
                    });
                    break;
            }
        }

        void ProcessInputs()
        {
            while (InputQueue.TryPeek(out var input))
            {
                // Too early → wait
                if (input.ClientTick > CurrentTick)
                    break;

                InputQueue.TryDequeue(out input);

                // Too late → discard
                if (input.ClientTick < CurrentTick - 5)
                    continue;

                ApplyValidatedInput(input);
            }
        }

        void BroadcastSnapshot()
        {
            var snapshot = new
            {
                type = "state",
                tick = CurrentTick,
                state = GameState.Snapshot()
            };

            Broadcast(snapshot);
        }

        public bool ValidateClientTick([NotNullWhen(true)] long? clientTick)
        {
            if (clientTick == null)
                return false;

            long delta = clientTick.Value - CurrentTick;

            // Too far in the future → cheating or broken client
            if (delta > 2)
                return false;

            // Too far in the past → replay or lag exploit
            if (delta < -5)
                return false;

            return true;
        }

        public void Broadcast(object json)
        {
            foreach (var m in _members.Values)
                m.Send(json);
        }

        public void Send(string to, object json)
        {
            var target = _members.Values.FirstOrDefault(c => c.Nick == to);
            target?.Send(json);
        }

        public void Join(ClientSession session)
        {
            _members[session.Id] = session;
        }

        public void Leave(ClientSession session)
        {
            _members.TryRemove(session.Id, out _);
            if(IsOwner(session) && !AutoClose && !IsEmpty)
            {
                Owner = _members.Values.First();
            }
            if(_members.Count <= 1 && Running)
            {
                Running = false;
                State = ChannelState.Lobby;
                CurrentTick = 0;
            }
        }
        public bool IsPasswordProtected => !string.IsNullOrEmpty(Password);
        public bool IsEmpty => _members.IsEmpty;
        public bool IsOwner(ClientSession s) => s.Id == Owner.Id;

        internal string[] GetMemberNicks()
        {
            return _members.Values.Select(c => c.Nick).ToArray();
        }
    }
}
