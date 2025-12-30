using System.Net;

namespace FMServer;

class Program
{
    private static Random random = new Random();

    public static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
    static void Main()
    {
        Console.Title = "Fazbear Multiplayer Server";
        int port = 7121;
        try {
            port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? port.ToString());
        }
        catch { }
        var server = new GameServer(IPAddress.Any, port);
        server.ServerSecret = (Environment.GetEnvironmentVariable("SERVER_SECRET") ?? RandomString(32)).Truncate(32);
        if (server.Start())
        {
            Console.WriteLine($"Server running on port {server.Port}");
            Console.WriteLine($"Server secret: {server.ServerSecret}");
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