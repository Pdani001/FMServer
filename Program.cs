using System.Net;

namespace FMServer;

class Program
{
    static void Main()
    {
        var server = new GameServer(IPAddress.Any, 8080);
        if (server.Start())
        {
            Console.WriteLine("Server running on ws://localhost:8080");
            Console.ReadLine();
            server.Stop();
        }
        else
        {
            Console.WriteLine("Failed to start server.");
            Console.ReadLine();
        }
    }
}