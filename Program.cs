using System.Net;

namespace FMServer;

class Program
{
    static void Main()
    {
        Console.Title = "Fazbear Multiplayer Server";
        int port = 7121;
        try {
            port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? port.ToString());
        }
        catch { }
        var server = new GameServer(IPAddress.Any, port);
        server.ServerSecret = (Environment.GetEnvironmentVariable("SERVER_SECRET") ?? "").Truncate(32);
        server.AdminName = Environment.GetEnvironmentVariable("ADMIN_NAME") ?? "";
        if (server.Start())
        {
            Console.WriteLine($"Server running on port {server.Port}");
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