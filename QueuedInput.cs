using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FMServer
{
    public class QueuedInput
    {
        public required ClientSession Client;
        public required Message Message;
        public required long ClientTick;
        public required long ReceivedAtTick;
    }
}
