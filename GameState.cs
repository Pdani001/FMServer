using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FMServer
{
    public class GameState
    {
        private readonly HashSet<Guid> readyPlayers = [];
        private readonly Dictionary<Guid, Character> playerCharacters = [];
        private readonly Dictionary<Character, int> characterMoveTimer = new()
        {
            { Character.Guard, 0 }
        };
        public static readonly Dictionary<Character, (int, int)> defaultMoveTimes = new()
        {
            { Character.Guard, (0, 0)},
            { Character.Freddy, (6, 15)},
            { Character.Bonnie, (10, 15)},
            { Character.Chica, (10, 18)},
            { Character.Foxy, (15, 30)},
        };
        private Dictionary<Character, (int, int)> customMoveTimes = new(defaultMoveTimes);
        private readonly Dictionary<Character, long> nextMoveOppurtunity = [];
        private readonly Dictionary<Character, int> characterPosition = new()
        {
            { Character.Guard, 0 },
            { Character.Freddy, 0 },
            { Character.Bonnie, -1 },
            { Character.Chica, -1 },
            { Character.Foxy, 0 }
        };
        public static readonly Dictionary<Character, int> defaultRobotAILevel = new()
        {
            { Character.Freddy, 3},
            { Character.Bonnie, 5},
            { Character.Chica, 7},
            { Character.Foxy, 5},
        };
        private readonly Dictionary<Character, int> robotAILevel = new(defaultRobotAILevel);
        private readonly Dictionary<Character, long> robotAttackTick = [];
        public int ReadyPlayerCount => readyPlayers.Count;
        public byte NightTime { get; set; } = 12;
        private int _power = 999;
        public int Power {
            get => _power;
            set
            {
                _power = value;
                if (_power < 0)
                {
                    _power = 0;
                }
            }
        }
        public bool IsCustomNight { get; set; } = false;
        public bool AllowAI { get; set; } = false;
        public bool PowerDown { get; set; } = false;
        public Character Jumpscared { get; set; } = Character.None;
        public bool ActiveJumpscare { get; set; } = false;

        public bool BlockRight { get; set; } = false;
        public bool RightDoor { get; set; } = false;
        public bool RightLight { get; set; } = false;
        public bool BlockLeft { get; set; } = false;
        public bool LeftDoor { get; set; } = false;
        public bool LeftLight { get; set; } = false;
        public bool CameraActive { get; set; } = false;
        public bool CameraGarble { get; set; } = false;

        public int FoxyAttempt { get; set; } = 0;
        public int Musicbox {  get; set; } = 0;

        public void ClearPlayers()
        {
            ResetReadyPlayers();
            playerCharacters.Clear();
        }

        public void ResetReadyPlayers()
        {
            readyPlayers.Clear();
        }

        public bool SetPlayerReady(Guid playerId, bool value = true)
        {
            if (!value)
            {
                readyPlayers.Remove(playerId);
                return true;
            }
            readyPlayers.Add(playerId);
            return true;
        }

        public bool IsPlayerReady(Guid playerId)
        {
            return readyPlayers.Contains(playerId);
        }

        public bool SetPlayerCharacter(Guid playerId, Character character)
        {
            if (IsPlayerReady(playerId))
                return false;
            if(character == Character.None)
            {
                playerCharacters.Remove(playerId);
                return true;
            }
            if (playerCharacters.ContainsValue(character))
            {
                return false;
            }
            playerCharacters[playerId] = character;
            return true;
        }

        public List<object> GetSelected()
        {
            return playerCharacters.Select(x=>new { Character = x.Value, Id = x.Key, Ready = IsPlayerReady(x.Key) } as object).ToList();
        }

        public long GetNextMoveOppurtunity(Character character)
        {
            return nextMoveOppurtunity.GetValueOrDefault(character, 0);
        }

        public void SetNextMoveOppurtunity(Character character, long tick)
        {
            if(!playerCharacters.ContainsValue(character) && character != Character.None && character != Character.Guard)
                nextMoveOppurtunity[character] = tick;
        }

        public void SetCustomMoveTime(Character character, int min, int max)
        {
            if (character == Character.None || character == Character.Guard)
                return;
            customMoveTimes[character] = (min, max);
        }

        public (int, int) GetCustomMoveTime(Character character)
        {
            return customMoveTimes.GetValueOrDefault(character, (0, 0));
        }

        public int GetCurrentMoveTime(Character character)
        {
            return characterMoveTimer.GetValueOrDefault(character, 1);
        }

        public int GetNewMoveTime(Character character)
        {
            if (!customMoveTimes.TryGetValue(character, out var timer))
                return 0;
            return GameServer.RNG.Next(timer.Item1, timer.Item2) * GameServer.TICK_RATE;
        }

        public void SetCharacterMoveTime(Character character, int timer)
        {
            if(playerCharacters.ContainsValue(character))
                characterMoveTimer[character] = timer;
        }

        public Character GetPlayerCharacter(Guid id)
        {
            if(!playerCharacters.TryGetValue(id, out var character))
            {
                return Character.None;
            }
            return character;
        }

        public Character[] GetActiveRobots()
        {
            return [.. GetPlayerRobots() , .. GetAIRobots()];
        }

        public Character[] GetPlayerRobots()
        {
            return [.. playerCharacters.Values.Where(c => c != Character.Guard)];
        }

        public Character[] GetAIRobots()
        {
            return [.. nextMoveOppurtunity.Keys];
        }

        public double GetAIMoveTime(Character character)
        {
            return character switch
            {
                Character.Freddy => 3.02,
                Character.Bonnie => 4.97,
                Character.Chica => 4.98,
                Character.Foxy => GetCharacterPosition(character) <= 2 ? 5.01 : 0,
                _ => 0
            };
        }

        public bool IsCharacterPlaying(Character character)
        {
            return playerCharacters.Values.Any(c => c == character);
        }

        public void SetCharacterPosition(Character character, int target)
        {
            if (character == Character.None)
                return;
            characterPosition[character] = target;
        }

        public int GetCharacterPosition(Character character)
        {
            return characterPosition.GetValueOrDefault(character, -1);
        }

        public long GetRobotAttack(Character character)
        {
            if (!GetPlayerRobots().Contains(character) && !GetAIRobots().Contains(character))
                return 0;
            return robotAttackTick.GetValueOrDefault(character, 0);
        }

        public void SetRobotAttack(Character character, long next)
        {
            if (!GetPlayerRobots().Contains(character) && !GetAIRobots().Contains(character))
                return;
            robotAttackTick[character] = next;
        }

        public void SetRobotAILevel(Character character, int level)
        {
            if (!robotAILevel.ContainsKey(character))
                return;
            robotAILevel[character] = Math.Clamp(level, 0, 20);
        }

        public int GetRobotAILevel(Character character)
        {
            return robotAILevel.GetValueOrDefault(character, 0);
        }

        public bool AttemptMove(Character character, int target)
        {
            if(target < 0)
            {
                return false;
            }
            int oldPos = GetCharacterPosition(character);
            bool update = true;
            switch (character)
            {
                case Character.Freddy:
                    switch (target)
                    {
                        case 6:
                        case 7:
                            if (GetCharacterPosition(Character.Chica) == target)
                                return false;
                            break;
                        case 1:
                            if (GetCharacterPosition(Character.Bonnie) == 0 || GetCharacterPosition(Character.Chica) == 0)
                                return false;
                            break;
                        case 21:
                            if((CameraActive && GetCharacterPosition(Character.Guard) == 7) || !CameraActive)
                                return false;
                            break;
                    }
                    break;
                case Character.Chica:
                    switch (target)
                    {
                        case 6:
                        case 7:
                            if (GetCharacterPosition(Character.Freddy) == target)
                                return false;
                            break;
                    }
                    break;
                case Character.Bonnie:
                    switch (target)
                    {
                        case 3:
                        case 21:
                            if (GetCharacterPosition(Character.Foxy) >= 3)
                                return false;
                            break;
                    }
                    break;
                case Character.Foxy:
                    if(GetAIRobots().Contains(Character.Foxy) && CameraActive)
                    {
                        return false;
                    }
                    if (target == 2)
                        target = oldPos + 1;
                    switch (target)
                    {
                        case 3:
                            if (GetCharacterPosition(Character.Bonnie) == 3 || GetCharacterPosition(Character.Bonnie) >= 21)
                                return false;
                            update = false;
                            break;
                        default:
                            SetRobotAttack(Character.Foxy, 0);
                            break;
                    }
                    break;
            }
            if (update)
                SetCharacterMoveTime(character, GetNewMoveTime(character));
            SetCharacterPosition(character, target);
            return true;
        }

        public int GetAITarget(Character character)
        {
            int current = GetCharacterPosition(character);
            return character switch
            {
                Character.Freddy => current switch
                {
                    0 => 1,
                    1 => 10,
                    10 => 9,
                    9 => 6,
                    6 => 7,
                    7 => 21,
                    _ => -1,
                },
                Character.Bonnie => current switch
                {
                    0 => GameServer.RNG.Next(2) == 1 ? 8 : 1,
                    1 => GameServer.RNG.Next(5) == 0 ? 8 : 3,
                    3 => GameServer.RNG.Next(2) == 1 ? 4 : 5,
                    5 => GameServer.RNG.Next(3) == 0 ? 3 : 21,
                    4 => GameServer.RNG.Next(3) == 0 ? 5 : 21,
                    8 => GameServer.RNG.Next(5) == 0 ? 1 : 3,
                    _ => -1,
                },
                Character.Chica => current switch
                {
                    0 => 1,
                    1 => GameServer.RNG.Next(2) == 1 ? 10 : 9,
                    9 => GameServer.RNG.Next(5) == 0 ? 10 : 6,
                    10 => GameServer.RNG.Next(5) == 0 ? 9 : 6,
                    6 => GameServer.RNG.Next(5) == 0 ? 1 : 7,
                    7 => GameServer.RNG.Next(3) == 0 ? 6 : 21,
                    _ => -1,
                },
                Character.Foxy => current switch
                {
                    0 => 2,
                    1 => 2,
                    2 => 3,
                    _ => -1
                },
                _ => -1,
            };
        }

        public int GetAttackReturnTarget(Character character)
        {
            return character switch
            {
                Character.Freddy => GetCharacterPosition(Character.Chica) == 6 ? 10 : 6,
                Character.Bonnie => 1,
                Character.Chica => GetCharacterPosition(Character.Freddy) == 6 ? 10 : GameServer.RNG.Next(2) == 1 ? 6 : 1,
                Character.Foxy => GameServer.RNG.Next(2),
                _ => -1,
            };
        }

        public bool IsValidMove(Character character, int target)
        {
            if(target < 0 || target > 10)
            {
                return false;
            }
            int current = GetCharacterPosition(character);
            return character switch
            {
                Character.Freddy => current switch
                {
                    0 => target == 1,
                    1 => target == 0
                        || target == 10
                        || target == 9
                        || target == 6,
                    10 => target == 1
                        || target == 9,
                    9 => target == 1
                        || target == 10
                        || target == 6,
                    6 => target == 9
                        || target == 7
                        || target == 1,
                    7 => target == 6,
                    _ => false,
                },
                Character.Chica => current switch
                {
                    0 => target == 1,
                    1 => target == 0
                        || target == 10
                        || target == 9
                        || target == 6,
                    10 => target == 1
                        || target == 9
                        || target == 6,
                    9 => target == 1
                        || target == 10
                        || target == 6,
                    6 => target == 9
                        || target == 7
                        || target == 1,
                    7 => target == 6,
                    _ => false,
                },
                Character.Bonnie => current switch
                {
                    0 => target == 1
                        || target == 8,
                    1 => target == 0
                        || target == 8
                        || target == 3,
                    8 => target == 1
                        || target == 3,
                    3 => target == 1
                        || target == 5
                        || target == 4,
                    5 => target == 3
                        || target == 4,
                    4 => target == 3
                        || target == 5,
                    _ => false,
                },
                Character.Foxy => current switch
                {
                    0 => target == 2,
                    1 => target == 2,
                    2 => target == 3,
                    _ => false
                },
                Character.Guard => true,
                _ => false,
            };
        }

        public object Snapshot ()
        {
            return new
            {
                time = NightTime,
                power = Power,
                right = new
                {
                    blocked = BlockRight,
                    door = RightDoor,
                    light = RightLight
                },
                left = new
                {
                    blocked = BlockLeft,
                    door = LeftDoor,
                    light = LeftLight
                },
                camera = new
                {
                    active = CameraActive,
                    garble = CameraGarble,
                },
            };
        }

    }
}
