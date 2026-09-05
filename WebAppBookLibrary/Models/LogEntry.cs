using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace WebAppBookLibrary.Models
{
    public class LogEntry
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public string? Username { get; set; }
        public string? Action { get; set; }
        public string? Controller { get; set; }
        public string? IP { get; set; }
        public string? Method { get; set; }
        public string? EventType { get; set; }
        public string? ActorId { get; set; }
        public string? TargetId { get; set; }
        public string? CorrelationId { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = [];
    }
}
