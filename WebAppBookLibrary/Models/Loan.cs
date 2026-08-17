using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models
{
    public class Loan
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public string BookId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        public DateTime LoanDate { get; set; } = DateTime.UtcNow;

        public DateTime? ReturnDate { get; set; }

        public bool IsReturned { get; set; } = false;
    }
}
