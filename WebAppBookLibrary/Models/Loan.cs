using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Loans;

namespace WebAppBookLibrary.Models
{
    public class Loan
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public string BookId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        public string MediaType { get; set; } = MediaTypes.Physical;
        public string Status { get; set; } = LoanStatuses.Active;
        public DateTime ReservedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DueAt { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public string? Notes { get; set; }

        // Legacy fields remain readable until the explicit schema migration is applied.
        public DateTime LoanDate { get; set; } = DateTime.UtcNow;

        public DateTime? ReturnDate { get; set; }

        public bool IsReturned { get; set; } = false;
    }
}
