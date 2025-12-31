namespace FMServer
{
    public class Message
    {
        public string? Session { get; set; }
        public required string Type { get; set; }
        public string? Nick { get; set; }
        public string? Channel { get; set; }
        public string? Text { get; set; }
        public int? Value { get; set; }
        public bool? Hidden { get; set; }
        public bool? AutoClose { get; set; }
        public long? Tick { get; set; }
        public bool? LeftSide { get; set; }
    }
}
