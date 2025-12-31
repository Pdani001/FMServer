using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FMServer
{
    public class QueuedInput
    {
        public ClientSession Client;
        public Message Message;
        public long ClientTick;
        public long ReceivedAtTick;
    }
}
