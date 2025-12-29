using System.Net;

namespace FMServer;

class Program
{
    static void Main()
    {
        Console.Title = "Fazbear Multiplayer Server";
        var server = new GameServer(IPAddress.Parse("127.0.0.1"), 6121);
        if (server.Start())
        {
            Console.WriteLine($"Server running on {server.Address}:{server.Port}");
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