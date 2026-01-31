using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FMServer
{
    public class Channel : IDisposable
    {
        ~Channel()
        {
            Console.WriteLine($"Channel {Name} finalized");
        }
        public string Name { get; }
        public ClientSession Owner { get; private set; }
        public ChannelState State = ChannelState.Lobby;
        public long CurrentTick { get; private set; } = 0;
        public bool Running {get; private set; }
        public bool Hidden { get; set; }

        public string Password { get; set; } = "";

        public int MaxPlayers { get; set; } = 5;
        private ConcurrentDictionary<Guid, ClientSession> _members = new();
        public readonly ConcurrentQueue<QueuedInput> InputQueue = new();
        private Thread? TickThread;

        public GameState GameState { get; private set; } = new();

        public Channel(string name, ClientSession owner, bool hidden)
        {
            Name = name;
            Owner = owner;
            Hidden = hidden;
            _members[owner.Id] = owner;
        }

        private CancellationTokenSource countdown = new();

        public void Abort()
        {
            if (State != ChannelState.Lobby || !IsCountdown)
                return;
            IsCountdown = false;
            countdown.Cancel();
            Broadcast(new
            {
                type = "game_countdown",
                value = -1
            });
        }

        public bool IsCountdown { get; private set; } = false;

        public void Countdown()
        {
            if (State != ChannelState.Lobby || IsCountdown)
                return;
            countdown = new();
            IsCountdown = true;
            Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

                for (int count = 5; count > 0; count--)
                {
                    if (countdown.IsCancellationRequested)
                        return;

                    Broadcast(new
                    {
                        type = "game_countdown",
                        value = count
                    });

                    await timer.WaitForNextTickAsync(countdown.Token);
                }
                if (!countdown.IsCancellationRequested)
                {
                    GameState.ResetReadyPlayers();
                    State = ChannelState.Starting;
                    IsCountdown = false;
                    Broadcast(new
                    {
                        type = "game_start"
                    });
                }
            });
        }

        public void Start()
        {
            if(State != ChannelState.Starting)
                return;
            IsCountdown = false;
            foreach (var character in GameState.GetPlayerRobots())
            {
                GameState.SetCharacterPosition(character, 0);
                GameState.SetCharacterMoveTimer(character, GameState.GetNewMoveTimer(character));
            }
            foreach (var character in Enum.GetValues<Character>())
            {
                var time = (long)Math.Round(GameServer.TICK_RATE * GameState.GetAIMoveTime(character), MidpointRounding.AwayFromZero);
                GameState.SetCharacterPosition(character, 0);
                GameState.SetNextMoveOppurtunity(character, time);
            }
            Broadcast(new
            {
                type = "game_start",
                positions = GameState.GetActiveRobots().Select(c => new { character = c, position = GameState.GetCharacterPosition(c) }).ToArray(),
            });
            foreach (var client in _members.Values.Where(c => GameState.GetPlayerRobots().Contains(GameState.GetPlayerCharacter(c.Id))))
            {
                client?.Send(new
                {
                    type = "move_timer",
                    value = Math.Round(((double)GameState.GetCurrentMoveTimer(GameState.GetPlayerCharacter(client.Id))) / GameServer.TICK_RATE, MidpointRounding.AwayFromZero)
                });
            }
            State = ChannelState.InGame;
            CurrentTick = 0;
            Running = true;

            TickThread = new Thread(RunChannelTickLoop);
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
                    long sleepMs = nextTickMs - elapsed;
                    if (sleepMs > 1)
                        Thread.Sleep((int)Math.Min(sleepMs, 5));
                    else
                        Thread.Sleep(1);
                }
            }
        }

        private void Tick()
        {
            CurrentTick++;

            ProcessInputs();
            UpdateLogic();
            AdvanceRobotMovement();
            BroadcastSnapshot();
        }

        private long lastNightTimeUpdateTick = 0;
        private long lastPowerUpdateTick = 0;
        private long startCameraGarbleTick = 0;
        private long lastMusicBoxTryTick = 0;
        private short MusicBoxTry = 0;

        private long ForceJumpscareTick = 0;
        private long FreddyJumpscareTick = 0;
        private long StartJumpscareTick = 0;

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
                UpdateAILevel();
                lastNightTimeUpdateTick = CurrentTick;
                if(GameState.NightTime == 6)
                {
                    State = ChannelState.Finished;
                    Running = false;
                    return;
                }
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
            if (GameState.CameraGarble && CurrentTick - startCameraGarbleTick >= GameServer.TICK_RATE * 3)
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
                                if (GameServer.RNG.Next(5) + 1 == 1 || MusicBoxTry == 4)
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
                                if (GameServer.RNG.Next(5) + 1 == 1 || MusicBoxTry == 10)
                                {
                                    MusicBoxTry = 0;
                                    GameState.Musicbox++;
                                    Running = false;
                                    State = ChannelState.Finished;
                                    GameState.Jumpscared = Character.Freddy;
                                    Broadcast(new
                                    {
                                        type = "musicbox",
                                        value = GameState.Musicbox
                                    });
                                }
                            }
                            break;
                    }
                }
                return;
            }
            foreach(var character in GameState.GetPlayerRobots().Where(c=>GameState.GetCurrentMoveTimer(c) > 0))
            {
                if (character == Character.Freddy && GameState.GetCharacterPosition(character) == 7 && ((GameState.CameraActive && GameState.GetCharacterPosition(Character.Guard) == 7) || !GameState.CameraActive))
                    continue;
                int timer = GameState.GetCurrentMoveTimer(character);
                timer -= 1;
                GameState.SetCharacterMoveTimer(character, timer);
                _members.Values.FirstOrDefault(c => GameState.GetPlayerCharacter(c.Id) == character)?.Send(new
                {
                    type = "move_timer",
                    value = Math.Round(((double)timer) / GameServer.TICK_RATE, MidpointRounding.AwayFromZero)
                });
            }
            int guardPos = GameState.GetCharacterPosition(Character.Guard);
            foreach (Character character in GameState.GetActiveRobots().Where(c=>c!=Character.Freddy && GameState.GetRobotAttack(c) > 0).OrderBy(GameState.GetRobotAttack))
            {
                int position = GameState.GetCharacterPosition(character);
                long attackTick = GameState.GetRobotAttack(character);
                int newpos = GameState.GetAttackReturnTarget(character);
                switch (character)
                {
                    case Character.Foxy:
                        newpos = 5;
                        if (position == 3)
                        {
                            if ((GameState.CameraGarble || (guardPos != 3 && GameState.CameraActive) || !GameState.CameraActive) && attackTick > CurrentTick)
                            {
                                continue;
                            }
                            if (guardPos == 3 && GameState.CameraActive)
                            {
                                Broadcast(new
                                {
                                    type = "foxy_run"
                                });
                                GameState.SetRobotAttack(character, CurrentTick + (long)Math.Round(GameServer.TICK_RATE * 1.7d, MidpointRounding.AwayFromZero));
                            }
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
                            GameState.SetRobotAttack(character, 0);
                            if (GameState.LeftDoor)
                            {
                                GameState.Power -= 10 + GameState.FoxyAttempt * 13;
                                GameState.FoxyAttempt++;
                                newpos = GameState.GetAttackReturnTarget(character);
                                GameState.SetCharacterPosition(character, newpos);
                                Broadcast(new
                                {
                                    type = "move",
                                    character,
                                    value = newpos
                                });
                                CheckCameraGarble(3, 2);
                                GameState.SetCharacterMoveTimer(character, GameState.GetNewMoveTimer(character));
                                double moveTime = GameState.GetAIMoveTime(character);
                                long newNextMoveTick = CurrentTick + (long)Math.Round(GameServer.TICK_RATE * moveTime, MidpointRounding.AwayFromZero);
                                GameState.SetNextMoveOppurtunity(character, newNextMoveTick);
                                continue;
                            }
                            GameState.SetNextMoveOppurtunity(character, 0);
                            GameState.BlockLeft = true;
                            GameState.LeftLight = false;
                            GameState.Jumpscared = character;
                            GameState.SetCharacterPosition(character, newpos);
                            Broadcast(new
                            {
                                type = "move",
                                character,
                                value = newpos
                            });
                            CheckCameraGarble(3, 21);
                            return;
                        }
                        continue;
                    case Character.Bonnie:
                        if (attackTick > CurrentTick || GameState.Jumpscared != Character.None)
                        {
                            continue;
                        }
                        if(!GameState.LeftDoor)
                        {
                            newpos = 22;
                            GameState.Jumpscared = character;
                            GameState.BlockLeft = true;
                        }
                        GameState.LeftLight = false;
                        break;
                    case Character.Chica:
                        if (attackTick > CurrentTick || GameState.Jumpscared != Character.None)
                        {
                            continue;
                        }
                        if (!GameState.RightDoor)
                        {
                            newpos = 22;
                            GameState.Jumpscared = character;
                            GameState.BlockRight = true;
                        }
                        GameState.RightLight = false;
                        break;
                }
                if(newpos <= 10)
                {
                    GameState.SetCharacterMoveTimer(character, GameState.GetNewMoveTimer(character));
                    double moveTime = GameState.GetAIMoveTime(character);
                    long newNextMoveTick = CurrentTick + (long)Math.Round(GameServer.TICK_RATE * moveTime, MidpointRounding.AwayFromZero);
                    GameState.SetNextMoveOppurtunity(character, newNextMoveTick);
                }
                CheckCameraGarble(position, newpos);
                GameState.SetCharacterPosition(character, newpos);
                GameState.SetRobotAttack(character, 0);
                Broadcast(new
                {
                    type = "move",
                    character,
                    value = newpos
                });
            }
            if(GameState.Jumpscared != Character.None)
            {
                if (!GameState.ActiveJumpscare)
                {
                    if (GameState.Jumpscared == Character.Freddy && !GameState.CameraActive && FreddyJumpscareTick == 0)
                    {
                        FreddyJumpscareTick = CurrentTick + (GameServer.TICK_RATE * 5);
                    }
                    if(GameState.CameraActive && ForceJumpscareTick == 0)
                    {
                        ForceJumpscareTick = CurrentTick + (GameServer.TICK_RATE * 15);
                    }
                    if((FreddyJumpscareTick != 0 && FreddyJumpscareTick <= CurrentTick) || (ForceJumpscareTick != 0 && ForceJumpscareTick <= CurrentTick) || GameState.Jumpscared == Character.Foxy)
                    {
                        StartJumpscareTick = CurrentTick;
                        GameState.ActiveJumpscare = true;
                        Broadcast(new
                        {
                            type = "jumpscare",
                            character = GameState.Jumpscared
                        });
                    }
                }
                else
                {
                    if(CurrentTick - StartJumpscareTick >= GameServer.TICK_RATE)
                    {
                        State = ChannelState.Finished;
                        Running = false;
                        Broadcast(new
                        {
                            type = "end_jumpscare"
                        });
                    }
                }
            }
        }

        private void UpdateAILevel()
        {
            switch (GameState.NightTime)
            {
                case 2:
                    GameState.SetRobotAILevel(Character.Bonnie, GameState.GetRobotAILevel(Character.Bonnie) + 1);
                    break;
                case 3:
                case 4:
                    GameState.SetRobotAILevel(Character.Bonnie, GameState.GetRobotAILevel(Character.Bonnie) + 1);
                    GameState.SetRobotAILevel(Character.Chica, GameState.GetRobotAILevel(Character.Chica) + 1);
                    GameState.SetRobotAILevel(Character.Foxy, GameState.GetRobotAILevel(Character.Foxy) + 1);
                    break;
            }
        }

        private void AdvanceRobotMovement()
        {
            if(GameState.NightTime == 6 || GameState.PowerDown)
                return;
            foreach (var character in GameState.GetAIRobots())
            {
                long nextMoveTick = GameState.GetNextMoveOppurtunity(character);
                if (nextMoveTick <= 0 || CurrentTick < nextMoveTick)
                {
                    continue;
                }
                int level = GameState.GetRobotAILevel(character);
                if (GameServer.RNG.Next(20) < level)
                {
                    int target = GameState.GetAITarget(character);
                    int oldPos = GameState.GetCharacterPosition(character);
                    if(target == 21)
                    {
                        target = StartAttack(character);
                    }
                    if(oldPos == 21)
                    {
                        target = CheckAIAttack(character);
                    }
                    if (GameState.AttemptMove(character, target))
                    {
                        target = GameState.GetCharacterPosition(character);
                        if (character == Character.Foxy && target == 3)
                            GameState.SetRobotAttack(character, CurrentTick + GameServer.TICK_RATE * 10);
                        Broadcast(new
                        {
                            type = "move",
                            character,
                            value = target
                        });
                        if (character == Character.Foxy)
                        {
                            switch (target)
                            {
                                case 0:
                                case 1:
                                case 2:
                                    if (oldPos < 3)
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
                    }
                }
                if(GameState.GetRobotAttack(character) > 0)
                {
                    GameState.SetNextMoveOppurtunity(character, 0);
                    continue;
                }
                double moveTime = GameState.GetAIMoveTime(character);
                long newNextMoveTick = CurrentTick + (long)Math.Round(GameServer.TICK_RATE * moveTime, MidpointRounding.AwayFromZero);
                GameState.SetNextMoveOppurtunity(character, newNextMoveTick);
            }
        }

        int CheckAIAttack(Character character)
        {
            int newpos = GameState.GetAttackReturnTarget(character);
            switch (character)
            {
                case Character.Bonnie:
                    if (GameState.Jumpscared != Character.None)
                    {
                        return -1;
                    }
                    if (!GameState.LeftDoor)
                    {
                        newpos = 22;
                        GameState.Jumpscared = character;
                        GameState.BlockLeft = true;
                    }
                    GameState.LeftLight = false;
                    break;
                case Character.Chica:
                    if (GameState.Jumpscared != Character.None)
                    {
                        return -1;
                    }
                    if (!GameState.RightDoor)
                    {
                        newpos = 22;
                        GameState.Jumpscared = character;
                        GameState.BlockRight = true;
                    }
                    GameState.RightLight = false;
                    break;
            }
            return newpos;
        }

        void ApplyValidatedInput(QueuedInput input)
        {
            Message msg = input.Message;
            switch (msg.Type)
            {
                case "cheat#power":
                    if (GameState.GetPlayerCharacter(input.Client.Id) != Character.Guard || GameState.Power <= 30)
                        return;
                    GameState.Power = 30;
                    break;
                case "cheat#time":
                    if (GameState.GetPlayerCharacter(input.Client.Id) != Character.Guard || GameState.NightTime >= 5)
                        return;
                    GameState.NightTime = 5;
                    lastNightTimeUpdateTick = CurrentTick - (GameServer.TICK_RATE * 80);
                    break;
                case "cheat#move":
                    if (GameState.GetPlayerCharacter(input.Client.Id) == Character.Guard || GameState.GetCurrentMoveTimer(GameState.GetPlayerCharacter(input.Client.Id)) <= 1)
                        return;
                    GameState.SetCharacterMoveTimer(GameState.GetPlayerCharacter(input.Client.Id), 1);
                    break;
                case "door":
                    if (GameState.GetPlayerCharacter(input.Client.Id) != Character.Guard || GameState.Power <= 0 || GameState.CameraActive)
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
                    if (GameState.GetPlayerCharacter(input.Client.Id) != Character.Guard || GameState.Power <= 0 || GameState.CameraActive)
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
                    if (GameState.GetPlayerCharacter(input.Client.Id) != Character.Guard || GameState.PowerDown || GameState.ActiveJumpscare)
                        return;
                    GameState.CameraActive = msg.Value == 1;
                    if (GameState.CameraActive)
                    {
                        GameState.LeftLight = false;
                        GameState.RightLight = false;
                    }
                    else if(GameState.Jumpscared != Character.None)
                    {
                        StartJumpscareTick = CurrentTick;
                        GameState.ActiveJumpscare = true;
                        Broadcast(new
                        {
                            type = "jumpscare",
                            character = GameState.Jumpscared
                        });
                    }
                    break;
                case "move":
                    var movechar = GameState.GetPlayerCharacter(input.Client.Id);
                    if ((movechar != Character.Guard && GameState.GetCurrentMoveTimer(movechar) > 0) || msg.Value == null)
                        return;
                    int target = msg.Value.Value;
                    if (!GameState.IsValidMove(movechar, target))
                    {
                        return;
                    }
                    if (movechar == Character.Guard)
                    {
                        if(target > 10)
                            target = 10;
                        GameState.CameraActive = true;
                        GameState.SetCharacterPosition(movechar, target);
                        return;
                    }
                    int oldPos = GameState.GetCharacterPosition(movechar);
                    if(!GameState.AttemptMove(movechar, target))
                    {
                        return;
                    }
                    target = GameState.GetCharacterPosition(movechar);
                    if (movechar == Character.Foxy && target == 3)
                        GameState.SetRobotAttack(Character.Foxy, CurrentTick + GameServer.TICK_RATE * 10);
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
                    movechar = GameState.GetPlayerCharacter(input.Client.Id);
                    if (movechar == Character.Guard || movechar == Character.Foxy || GameState.GetCurrentMoveTimer(movechar) > 0 || GameState.Jumpscared != Character.None)
                        return;
                    target = StartAttack(movechar);
                    if(target != -1)
                        Broadcast(new
                        {
                            type = "move",
                            character = movechar,
                            value = target
                        });
                    break;
            }
        }

        int StartAttack(Character movechar)
        {
            int oldPos = GameState.GetCharacterPosition(movechar);
            int target = 21;
            double moveTime = GameState.GetAIMoveTime(movechar);
            long newNextMoveTick = CurrentTick + (long)Math.Round(GameServer.TICK_RATE * moveTime, MidpointRounding.AwayFromZero);
            switch (movechar)
            {
                case Character.Freddy:
                    if (oldPos != 7) return -1;
                    if(GameState.GetAIRobots().Contains(movechar) && ((GameState.CameraActive && GameState.GetCharacterPosition(Character.Guard) == 7) || !GameState.CameraActive))
                    {
                        return -1;
                    }
                    if (GameState.RightDoor)
                    {
                        target = GameState.GetAttackReturnTarget(movechar);
                        GameState.SetCharacterMoveTimer(movechar, GameState.GetNewMoveTimer(movechar));
                    }
                    else
                    {
                        GameState.RightLight = false;
                        GameState.BlockRight = true;
                        GameState.Jumpscared = movechar;
                    }
                    GameState.SetCharacterPosition(movechar, target);
                    break;
                case Character.Chica:
                    if (oldPos != 7) return -1;
                    GameState.SetCharacterPosition(movechar, target);
                    if(GameState.GetPlayerRobots().Contains(movechar))
                    {
                        GameState.SetRobotAttack(movechar, newNextMoveTick);
                    }
                    GameState.RightLight = false;
                    break;
                case Character.Bonnie:
                    if (oldPos != 4 || GameState.GetCharacterPosition(Character.Foxy) >= 3) return -1;
                    GameState.SetCharacterPosition(movechar, target);
                    if (GameState.GetPlayerRobots().Contains(movechar))
                    {
                        GameState.SetRobotAttack(movechar, newNextMoveTick);
                    }
                    GameState.LeftLight = false;
                    break;
            }
            CheckCameraGarble(oldPos, target);
            return target;
        }

        void CheckCameraGarble(int oldPos, int target)
        {
            if (!GameState.CameraActive)
                return;
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
                if (input.ClientTick > CurrentTick)
                    break;

                InputQueue.TryDequeue(out input);

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

            if (delta < -5 || delta > 2)
                return false;

            return true;
        }

        readonly object ServerClient = new
        {
            id = Guid.Empty,
            nick = "FMServer"
        };

        public void HandleCommand(GameServer server, ClientSession sender, string command, string[] args)
        {
            var text = "";
            switch (command)
            {
                case "help":
                    text = "Available commands:"
                        + "\n- /help - View this list"
                        + "\n- /list - List all players";
                    if (IsOwner(sender))
                    {
                        text += "\n- /pw [password] - Sets or unsets the lobby password"
                            + "\n- /kick <nick> - Kicks the first player with the given nickname";
                    }
                    break;
                case "list":
                    text = $"Currently playing ({_members.Count}/{MaxPlayers}):"
                        + "\n"
                        + string.Join("\n",_members.Select(kvp=>$"- {kvp.Value.Nick}"+(kvp.Key == sender.Id ? " (you)" : IsOwner(kvp.Value) ? " (owner)" : "")));
                    break;
                case "pw":
                    if (!IsOwner(sender))
                        break;
                    if(args.Length == 0)
                    {
                        if(Password != "")
                        {
                            Password = "";
                            text = "Lobby password removed.";
                        }
                        else
                        {
                            text = "Usage: /pw [password]";
                        }
                        break;
                    }
                    Password = string.Join(' ', args);
                    text = "Lobby password set.";
                    break;
                case "kick":
                    if (!IsOwner(sender))
                        break;
                    if (args.Length == 0)
                    {
                        text = "Usage: /kick <nick>";
                        break;
                    }
                    var search = _members.Where(m => m.Value.Nick == args[0]);
                    if (search.Any())
                    {
                        int index = search.First().Key == sender.Id ? 1 : 0;
                        if(index >= search.Count())
                        {
                            text = "You can't kick yourself.";
                            break;
                        }
                        server.LeaveChannel(search.ElementAt(index).Value, "Kicked by Owner");
                        text = "Kicked player from the lobby.";
                        break;
                    }
                    text = "No player found with the given nick.";
                    break;
                case "aitest":
                    if (!IsOwner(sender))
                        break;
                    foreach(var character in Enum.GetValues<Character>())
                    {
                        GameState.SetRobotAILevel(character, 20);
                    }
                    text = "AI levels set to 20 for testing.";
                    break;
            }
            if(text != "")
                sender.Send(new
                {
                    type = "chat",
                    client = ServerClient,
                    text,
                    isadmin = true
                });
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
            if(IsCountdown)
                Abort();
        }

        public void Leave(ClientSession session)
        {
            _members.TryRemove(session.Id, out _);
            if(IsOwner(session) && !IsEmpty)
            {
                Owner = _members.Values.First();
                Broadcast(new
                {
                    type = "change_owner",
                    client = Owner.Info
                });
            }
            // this order must be maintained or everything breaks
            Character character = GameState.GetPlayerCharacter(session.Id);
            GameState.SetPlayerReady(session.Id, false);
            GameState.SetPlayerCharacter(session.Id, Character.None);
            if (IsCountdown)
                Abort();
            if ((_members.Count <= 1 || character == Character.Guard) && (Running || State == ChannelState.Starting))
            {
                Running = false;
                State = ChannelState.Lobby;
                GameState = new();
                CurrentTick = 0;
                InputQueue.Clear();
                Broadcast(new
                {
                    type = "game_abort"
                });
            }
        }
        public bool IsPasswordProtected => !string.IsNullOrEmpty(Password);
        public bool IsEmpty => _members.IsEmpty;
        public bool IsOwner(ClientSession s) => Owner != null && s.Id == Owner.Id;

        internal List<object> GetMembers()
        {
            return _members.Values.Select(c => c.Info).ToList();
        }

        public void Dispose()
        {
            // Prevent double disposal
            if (State == ChannelState.Disposing)
                return;

            Console.WriteLine($"[DISPOSE] Channel {Name}");

            State = ChannelState.Disposing;

            // Stop countdown
            IsCountdown = false;
            countdown.Cancel();

            // Stop tick loop
            Running = false;

            if (TickThread != null)
            {
                TickThread.Join();
                TickThread = null;
            }

            // Detach players
            foreach (var member in _members.Values)
            {
                member.CurrentChannel = null;
            }

            _members.Clear();
            InputQueue.Clear();
            Owner = null!;

            GameState.ClearPlayers();

            GameState = null!;
        }
    }
}
