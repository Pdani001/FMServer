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
