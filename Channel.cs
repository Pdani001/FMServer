using System.Collections.Concurrent;

namespace FMServer
{
    public class Channel
    {
        public string Name { get; }
        public ClientSession Owner { get; private set; }
        public bool Hidden { get; }
        public bool AutoClose { get; }

        public string Password { get; set; } = "";

        private ConcurrentDictionary<Guid, ClientSession> _members = new();

        public Channel(string name, ClientSession owner, bool hidden, bool autoClose)
        {
            Name = name;
            Owner = owner;
            Hidden = hidden;
            AutoClose = autoClose;
            _members[owner.Id] = owner;
        }
        
        public void Broadcast(string sender, string json)
        {
            foreach (var m in _members.Values.Where(c => c.Nick != sender))
                m.SendTextAsync(json);
        }

        public void Send(string to, string json)
        {
            var target = _members.Values.FirstOrDefault(c => c.Nick == to);
            target?.SendTextAsync(json);
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
