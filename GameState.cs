using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FMServer
{
    public class GameState
    {
        private HashSet<string> readyPlayers = [];
        private readonly Dictionary<string, Character> playerCharacters = [];
        private readonly Dictionary<Character, int> characterMoveTimer = [];
        private readonly Dictionary<Character, int> characterPosition = [];
        public int ReadyPlayerCount => readyPlayers.Count;
        public byte NightTime { get; set; } = 12;
        public int Power { get; set; } = 999;

        public bool BlockRight { get; set; } = false;
        public bool RightDoor { get; set; } = false;
        public bool RightLight { get; set; } = false;
        public bool BlockLeft { get; set; } = false;
        public bool LeftDoor { get; set; } = false;
        public bool LeftLight { get; set; } = false;
        public bool CameraActive { get; set; } = false;

        public void ResetReadyPlayers()
        {
            readyPlayers.Clear();
        }

        public void SetPlayerReady(string playerNick, bool value = true)
        {
            if (!value)
            {
                readyPlayers.Remove(playerNick);
                return;
            }
            readyPlayers.Add(playerNick);
        }

        public bool SetPlayerCharacter(string playerNick, Character character)
        {
            if(character == Character.None)
            {
                playerCharacters.Remove(playerNick);
                return true;
            }
            if (playerCharacters.ContainsValue(character))
            {
                return false;
            }
            playerCharacters[playerNick] = character;
            return true;
        }

        public int GetCharacterMoveTimer(Character character)
        {
            if(!characterMoveTimer.TryGetValue(character, out var timer))
            {
                return 1;
            }
            return timer;
        }

        public void SetCharacterMoveTimer(Character character, int timer)
        {
            characterMoveTimer[character] = timer;
        }

        public Character GetPlayerCharacter(string nick)
        {
            if(!playerCharacters.TryGetValue(nick, out var character))
            {
                return Character.None;
            }
            return character;
        }

        public Character[] GetPlayingRobots()
        {
            return playerCharacters.Values.Where(c => c != Character.Guard).ToArray();
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

        public bool IsValidPosition(Character character, int position)
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
                camera = CameraActive
            };
        }

    }
}
