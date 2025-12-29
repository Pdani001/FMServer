namespace FMServer
{
    public class Message
    {
        public required string type { get; set; }
        public byte? subchannel { get; set; }
        public string? nick { get; set; }
        public string? channel { get; set; }
        public string? text { get; set; }
        public int? value { get; set; }
        public bool? hidden { get; set; }
        public bool? autoclose { get; set; }
        public bool? echo { get; set; }
        public string? to { get; set; }
    }
}
