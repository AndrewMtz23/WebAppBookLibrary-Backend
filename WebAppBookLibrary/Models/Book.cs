using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models
{
    public class Book
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;
 
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string Genre { get; set; } = string.Empty;
        public bool IsAvailable { get; set; } = true;
    }

}
