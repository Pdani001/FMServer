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
        private readonly Dictionary<Character, (int, int)> defMoveTimes = new()
        {
            { Character.Guard, (0, 0)},
            { Character.Freddy, (10, 25)},
            { Character.Bonnie, (10, 15)},
            { Character.Chica, (10, 20)},
            { Character.Foxy, (15, 35)},
        };
        private readonly Dictionary<Character, int> characterPosition = new()
        {
            { Character.Guard, 0 },
            { Character.Freddy, 0 },
            { Character.Bonnie, -1 },
            { Character.Chica, -1 },
            { Character.Foxy, 0 }
        };
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

        public int GetCurrentMoveTimer(Character character)
        {
            return characterMoveTimer.GetValueOrDefault(character, 1);
        }

        public int GetNewMoveTimer(Character character)
        {
            if (!defMoveTimes.TryGetValue(character, out var timer))
                return 0;
            return GameServer.RNG.Next(timer.Item1, timer.Item2) * GameServer.TICK_RATE;
        }

        public void SetCharacterMoveTimer(Character character, int timer)
        {
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

        public Character[] GetPlayingRobots()
        {
            return playerCharacters.Values.Where(c => c != Character.Guard).ToArray();
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
            if (!GetPlayingRobots().Contains(character))
                return 0;
            return robotAttackTick.GetValueOrDefault(character, 0);
        }

        public void SetRobotAttack(Character character, long next)
        {
            if (!GetPlayingRobots().Contains(character))
                return;
            robotAttackTick[character] = next;
        }

        public bool IsValidMove(Character character, int position)
        {
            if(position < 0 || position > 10)
            {
                return false;
            }
            int current = GetCharacterPosition(character);
            return character switch
            {
                Character.Freddy or Character.Chica => current switch
                {
                    0 => position == 1,
                    1 => position == 0
                        || position == 10
                        || position == 9
                        || position == 6,
                    10 => position == 1
                        || position == 9,
                    9 => position == 1
                        || position == 10
                        || position == 6,
                    6 => position == 9
                        || position == 7
                        || position == 1,
                    7 => position == 6,
                    _ => false,
                },
                Character.Bonnie => current switch
                {
                    0 => position == 1,
                    1 => position == 0
                        || position == 8
                        || position == 3,
                    8 => position == 1,
                    3 => position == 1
                        || position == 5
                        || position == 4,
                    5 => position == 3
                        || position == 4,
                    4 => position == 3
                        || position == 5,
                    _ => false,
                },
                Character.Foxy => current switch
                {
                    0 => position == 1,
                    1 => position == 2,
                    2 => position == 3,
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
