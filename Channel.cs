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
        private readonly Random rng = new(Guid.NewGuid().GetHashCode());

        public GameState GameState { get; private set; } = new();

        public Channel(string name, ClientSession owner, bool hidden, bool autoClose)
        {
            Name = name;
            Owner = owner;
            Hidden = hidden;
            AutoClose = autoClose;
            _members[owner.Id] = owner;
        }

        private CancellationTokenSource countdown = new();

        public void Abort()
        {
            if (State != ChannelState.Starting)
                return;
            countdown.Cancel();
            countdown = new();
            State = ChannelState.Lobby;
            Broadcast(new
            {
                type = "game_countdown",
                countdown = -1
            });
        }

        public void Countdown()
        {
            if (State != ChannelState.Lobby)
                return;
            State = ChannelState.Starting;
            Task.Run(() =>
            {
                int count = 5;
                while(count > 0 && !countdown.IsCancellationRequested)
                {
                    Broadcast(new
                    {
                        type = "game_countdown",
                        countdown = count
                    });
                    Thread.Sleep(1000);
                    count--;
                }
                if(count == 0 && !countdown.IsCancellationRequested)
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

        List<long> lag = new(5);

        private void RunChannelTickLoop()
        {
            var stopwatch = Stopwatch.StartNew();
            long nextTickMs = 0;

            while (Running)
            {
                long elapsed = stopwatch.ElapsedMilliseconds;

                if (elapsed >= nextTickMs)
                {
                    lag = lag[1..lag.Capacity];
                    lag.Add(elapsed);
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
            
            if(CurrentTick % (GameServer.TICK_RATE * lag.Capacity) == 0)
            {
                Console.WriteLine($"[{DateTime.Now}] Current tick drift: {lag.Average()}");
            }

            ProcessInputs();
            UpdateLogic();
            BroadcastSnapshot();
        }

        private long lastNightTimeUpdateTick = 0;
        private long lastPowerUpdateTick = 0;
        private long startCameraGarbleTick = 0;
        private long lastMusicBoxTryTick = 0;
        private short MusicBoxTry = 0;

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
                lastPowerUpdateTick = CurrentTick;
            }
            if (CurrentTick - startCameraGarbleTick >= GameServer.TICK_RATE * 3)
            {
                GameState.CameraGarble = false;
            }
            if (GameState.Power == 0)
            {
                if (!GameState.PowerDown)
                {
                    GameState.PowerDown = true;
                    GameState.LeftDoor = false;
                    GameState.RightDoor = false;
                    GameState.LeftLight = false;
                    GameState.RightLight = false;
                    GameState.CameraActive = false;
                    lastMusicBoxTryTick = CurrentTick;
                }
                else
                {
                    switch (GameState.Musicbox)
                    {
                        case 0:
                        case 1:
                            if (CurrentTick - lastMusicBoxTryTick >= GameServer.TICK_RATE * 5)
                            {
                                lastMusicBoxTryTick = CurrentTick;
                                MusicBoxTry++;
                                Debug.WriteLine($"Music Box State: {GameState.Musicbox}, Try: {MusicBoxTry}");
                                if (rng.Next(5) + 1 == 1 || MusicBoxTry == 4)
                                {
                                    MusicBoxTry = 0;
                                    GameState.Musicbox++;
                                    Broadcast(new
                                    {
                                        type = "musicbox",
                                        value = GameState.Musicbox
                                    });
                                }
                            }
                            break;
                        case 2:
                            if (CurrentTick - lastMusicBoxTryTick >= Math.Round(GameServer.TICK_RATE * 0.3d, MidpointRounding.AwayFromZero))
                            {
                                lastMusicBoxTryTick = CurrentTick;
                                MusicBoxTry = 0;
                                GameState.Musicbox++;
                                Broadcast(new
                                {
                                    type = "musicbox",
                                    value = GameState.Musicbox
                                });
                            }
                            break;
                        case 3:
                            if (CurrentTick - lastMusicBoxTryTick >= GameServer.TICK_RATE * 2)
                            {
                                lastMusicBoxTryTick = CurrentTick;
                                MusicBoxTry++;
                                Debug.WriteLine($"Music Box State: {GameState.Musicbox}, Try: {MusicBoxTry}");
                                if (rng.Next(5) + 1 == 1 || MusicBoxTry == 4)
                                {
                                    MusicBoxTry = 0;
                                    GameState.Musicbox++;
                                }
                            }
                            break;
                    }
                    if(GameState.Musicbox < 3)
                    {
                        if(CurrentTick - lastMusicBoxTryTick >= GameServer.TICK_RATE * 5)
                        {
                            lastMusicBoxTryTick = CurrentTick;
                            MusicBoxTry++;
                            Debug.WriteLine($"Music Box State: {GameState.Musicbox}, Try: {MusicBoxTry}");
                            if (rng.Next(5) + 1 == 1 || MusicBoxTry == 4)
                            {
                                MusicBoxTry = 0;
                                GameState.Musicbox++;
                            }
                        }
                    }
                }
                return;
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
            int guardPos = GameState.GetCharacterPosition(Character.Guard);
            foreach (Character character in GameState.GetPlayingRobots().Where(c=>c!=Character.Freddy).OrderBy(GameState.GetRobotAttack))
            {
                int position = GameState.GetCharacterPosition(character);
                long attackTick = GameState.GetRobotAttack(character);
                int newpos = 1;
                switch (character)
                {
                    case Character.Foxy:
                        newpos = 5;
                        if (position == 3)
                        {
                            if ((GameState.CameraGarble || (guardPos != 3 && GameState.CameraActive)) && attackTick > CurrentTick)
                            {
                                return;
                            }
                            if (guardPos == 3 && GameState.CameraActive)
                                GameState.SetRobotAttack(character, CurrentTick + (long)Math.Round(GameServer.TICK_RATE * 1.7d, MidpointRounding.AwayFromZero));
                            GameState.SetCharacterPosition(character, 4);
                            Broadcast(new
                            {
                                type = "move",
                                character,
                                value = 4
                            });
                        }
                        else if (position == 4 && attackTick <= CurrentTick)
                        {
                            if (GameState.LeftDoor)
                            {
                                GameState.Power -= 10 + GameState.FoxyAttempt * 13;
                                GameState.FoxyAttempt++;
                                newpos = rng.Next(2);
                                GameState.SetCharacterPosition(character, newpos);
                                Broadcast(new
                                {
                                    type = "move",
                                    character,
                                    value = newpos
                                });
                                CheckCameraGarble(3, 2);
                                return;
                            }
                            GameState.Jumpscared = character;
                            GameState.SetCharacterPosition(character, newpos);
                            Broadcast(new
                            {
                                type = "move",
                                character,
                                value = newpos
                            });
                            CheckCameraGarble(3, 21);
                        }
                        return;
                    case Character.Bonnie:
                        newpos = 1;
                        if (attackTick > CurrentTick)
                        {
                            return;
                        }
                        if(!GameState.LeftDoor)
                        {
                            newpos = 22;
                            GameState.Jumpscared = character;
                        }
                        break;
                    case Character.Chica:
                        newpos = 1;
                        if (attackTick > CurrentTick)
                        {
                            return;
                        }
                        if (!GameState.RightDoor)
                        {
                            newpos = 22;
                            GameState.Jumpscared = character;
                        }
                        break;
                }
                CheckCameraGarble(position, newpos);
                GameState.SetCharacterPosition(character, newpos);
                Broadcast(new
                {
                    type = "move",
                    character,
                    value = newpos
                });
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
                    if ((movechar != Character.Guard && GameState.GetCharacterMoveTimer(movechar) > 0) || msg.Value == null)
                        return;
                    int target = msg.Value.Value;
                    if (!GameState.IsValidMove(movechar, target))
                    {
                        return;
                    }
                    if (movechar == Character.Guard)
                    {
                        GameState.CameraActive = true;
                        GameState.SetCharacterPosition(movechar, target);
                        return;
                    }
                    int oldPos = GameState.GetCharacterPosition(movechar);
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
                                    if (GameState.GetCharacterPosition(Character.Bonnie) == 3 || GameState.GetCharacterPosition(Character.Bonnie) >= 21)
                                        return;
                                    GameState.SetRobotAttack(Character.Foxy, CurrentTick + GameServer.TICK_RATE * 10);
                                    break;
                                default:
                                    GameState.SetRobotAttack(Character.Foxy, 0);
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
                    if(movechar == Character.Foxy)
                    {
                        switch (target)
                        {
                            case 0:
                            case 1:
                            case 2:
                                if(oldPos < 3)
                                {
                                    oldPos = 2;
                                }
                                else
                                {
                                    oldPos = 3;
                                }
                                target = 2;
                                break;
                            case 3:
                                oldPos = 2;
                                target = 3;
                                break;
                        }
                    }
                    CheckCameraGarble(oldPos, target);
                    break;
                case "attack":
                    movechar = GameState.GetPlayerCharacter(input.Client.Nick);
                    if (movechar == Character.Guard || movechar == Character.Foxy || GameState.GetCharacterMoveTimer(movechar) > 0 || GameState.Jumpscared != Character.None)
                        return;
                    int position = GameState.GetCharacterPosition(movechar);
                    target = 21;
                    switch (movechar)
                    {
                        case Character.Freddy:
                            if(position != 7) return;
                            if (GameState.RightDoor)
                            {
                                target = 1;
                            }
                            else
                                GameState.Jumpscared = movechar;
                            GameState.SetCharacterPosition(movechar, target);
                            break;
                        case Character.Chica:
                            if (position != 7 || GameState.GetRobotAttack(Character.Bonnie) > CurrentTick) return;
                            GameState.SetCharacterPosition(movechar, target);
                            GameState.SetRobotAttack(movechar, CurrentTick + GameServer.TICK_RATE * 6);
                            break;
                        case Character.Bonnie:
                            if (position != 4 || GameState.GetRobotAttack(Character.Chica) > CurrentTick) return;
                            GameState.SetCharacterPosition(movechar, target);
                            GameState.SetRobotAttack(movechar, CurrentTick + GameServer.TICK_RATE * 6);
                            break;
                    }
                    Broadcast(new
                    {
                        type = "move",
                        character = movechar,
                        value = target
                    });
                    break;
            }
        }

        void CheckCameraGarble(int oldPos, int target)
        {
            int guardPos = GameState.GetCharacterPosition(Character.Guard);
            if ((target == guardPos || oldPos == guardPos) && !GameState.CameraGarble)
            {
                GameState.CameraGarble = true;
                startCameraGarbleTick = CurrentTick;
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
                if (input!.ClientTick < CurrentTick - 5)
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
