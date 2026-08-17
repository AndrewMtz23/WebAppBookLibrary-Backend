using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;

namespace WebAppBookLibrary.Models
{
    public class User
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;

        // Roles: Admin, User, Accountant, Operator
        public string Role { get; set; } = "User"; // Default role is User
    }
}
