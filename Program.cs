using System.Net;

namespace FMServer;

class Program
{
    static void Main()
    {
        Console.Title = "Fazbear Multiplayer Server";
        Console.WriteLine();
        int port = 7121;
        try {
            port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? port.ToString());
        }
        catch { }
        var server = new GameServer(IPAddress.Any, port)
        {
            ServerSecret = (Environment.GetEnvironmentVariable("SERVER_SECRET") ?? "").Truncate(32),
            AdminName = Environment.GetEnvironmentVariable("ADMIN_NAME") ?? ""
        };
        if (server.Start())
        {
            Console.WriteLine($"[{DateTime.Now}] Listening on port {server.Port}");
            bool stop = false;
            while (true)
            {
                string? text = Console.ReadLine()?.ToLower();
                if ((text == "stop" && !server.ChannelInGame) || (text == "yes" && stop))
                    break;
                if (text == "stop" && server.ChannelInGame)
                {
                    Console.WriteLine("There is one or more lobbies in-game, if you are sure you want to stop the server, type in 'yes'");
                    stop = true;
                }
            }
            server.Stop();
        }
        else
        {
            Console.WriteLine("Failed to start server. Is the port available?");
            Console.ReadLine();
        }
    }
}