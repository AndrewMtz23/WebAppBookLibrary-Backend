# WebAppBookLibrary - Backend

## Overview

WebAppBookLibrary is a secure ASP.NET Core REST API for managing a digital library system. It provides authentication, book management, loan tracking, and comprehensive logging capabilities. The project demonstrates best practices for secure backend development in .NET.

## Features

- JWT-based authentication and authorization
- Secure password hashing and validation
- Email validation
- Book management (CRUD operations)
- Loan tracking system
- Comprehensive logging and audit trails
- MongoDB Atlas integration
- CORS configuration for frontend integration
- Development and production environment separation

## Technology Stack

- .NET 8
- ASP.NET Core
- MongoDB Atlas
- JWT Authentication
- C#

## Project Structure

```
WebAppBookLibrary/
├── Controllers/           # API endpoints
│   ├── AuthController.cs         # Authentication endpoints
│   ├── BooksController.cs        # Book management endpoints
│   ├── LoansController.cs        # Loan management endpoints
│   └── LogController.cs          # Logging endpoints
├── Services/             # Business logic and utilities
│   ├── BookService.cs           # Book service logic
│   ├── LoanService.cs           # Loan service logic
│   ├── UserService.cs           # User service logic
│   ├── MongoDBService.cs        # MongoDB connection and operations
│   ├── PasswordHasher.cs        # Password hashing utilities
│   ├── PasswordValidator.cs     # Password validation rules
│   ├── EmailValidator.cs        # Email validation logic
│   ├── DummyAuthHandler.cs      # Development authentication
│   └── Logservice.cs            # Logging service
├── Models/               # Data models
│   ├── User.cs                  # User data model
│   ├── Book.cs                  # Book data model
│   ├── Loan.cs                  # Loan data model
│   └── LogEntry.cs              # Log entry model
├── Data/                 # Database context
│   └── ApplicationDbContext.cs  # Database configuration
├── Properties/           # Project properties
│   └── launchSettings.json      # Launch configuration
├── appsettings.json             # Default configuration
├── appsettings.Development.json # Development configuration (git-ignored)
├── Program.cs                   # Application entry point and dependency injection
├── WebAppBookLibrary.csproj    # Project file
└── WebAppBookLibrary.http      # HTTP request file for testing

```

## Prerequisites

- .NET 8 SDK
- MongoDB Atlas account
- Visual Studio, Visual Studio Code, or Rider (optional)

## Getting Started

### 1. Clone the Repository

```bash
git clone <repository-url>
cd WebAppBookLibrary
```

### 2. Environment Configuration

Create a `.env` file in the project root directory based on `.env.example`:

```bash
cp .env.example .env
```

Edit the `.env` file with your configuration:

```env
# MongoDB Configuration
MONGO_USER=your_mongodb_atlas_username
MONGO_PASSWORD=your_mongodb_atlas_password
MONGO_CLUSTER=your_cluster_name.mongodb.net
MONGO_DATABASE=LibrarySecurityDb

# JWT Configuration (Change the key for production)
JWT_KEY=your-secure-secret-key-minimum-32-characters
JWT_ISSUER=WebAppBookLibrary
JWT_AUDIENCE=WebAppBookLibraryUsers

# CORS Configuration
CORS_ORIGIN=http://localhost:4200

# Environment
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=https://localhost:7086
```

### 3. Install Dependencies

```bash
dotnet restore
```

### 4. Build the Project

```bash
dotnet build
```

### 5. Run the Application

```bash
dotnet run
```

The API will be available at `https://localhost:7086`

### 6. Access API Documentation

Swagger UI will be available at `https://localhost:7086/swagger` (Development environment only)

## API Endpoints

### Authentication

- `POST /api/auth/register` - Register a new user
- `POST /api/auth/login` - Login and obtain JWT token
- `POST /api/auth/refresh` - Refresh JWT token

### Books

- `GET /api/books` - Get all books
- `GET /api/books/{id}` - Get book by ID
- `POST /api/books` - Create a new book (Admin only)
- `PUT /api/books/{id}` - Update a book (Admin only)
- `DELETE /api/books/{id}` - Delete a book (Admin only)
- `GET /api/books/search` - Search books

### Loans

- `GET /api/loans` - Get user's loans
- `GET /api/loans/{id}` - Get loan details
- `POST /api/loans` - Create a new loan request
- `PUT /api/loans/{id}/return` - Return a book

### Logs

- `GET /api/logs` - Get system logs (Admin only)
- `GET /api/logs/{id}` - Get specific log entry

## Environment Variables Explanation

### MongoDB Configuration

- `MONGO_USER`: Your MongoDB Atlas username
- `MONGO_PASSWORD`: Your MongoDB Atlas password
- `MONGO_CLUSTER`: Your MongoDB Atlas cluster name (e.g., cluster0.v3fhn.mongodb.net)
- `MONGO_DATABASE`: Database name to use (e.g., LibrarySecurityDb)

### JWT Configuration

- `JWT_KEY`: Secret key for signing JWT tokens (minimum 32 characters, use a strong random value in production)
- `JWT_ISSUER`: Token issuer identifier
- `JWT_AUDIENCE`: Token audience identifier

### Security Notes

- Never commit `.env` file to version control
- Use strong, unique JWT keys in production
- Rotate credentials regularly
- Use environment-specific configuration for different deployment stages

## Configuration Files

### appsettings.json

Default application settings including logging levels and allowed hosts.

### appsettings.Development.json

Development-specific settings (git-ignored). Override production settings here.

### .env

Runtime environment variables loaded by DotNetEnv package. Contains sensitive credentials and deployment-specific settings.

## Running Tests

```bash
dotnet test
```

## Development

### Database Migrations

To create or update database schema:

```bash
dotnet ef migrations add MigrationName
dotnet ef database update
```

### Code Style

The project uses standard C# conventions. Use EditorConfig (`.editorconfig`) if provided.

## Production Deployment

When deploying to production:

1. Build the release version:
   ```bash
   dotnet publish -c Release -o ./publish
   ```

2. Set production environment variables securely (use Azure Key Vault, AWS Secrets Manager, etc.)

3. Ensure JWT_KEY is a strong, cryptographically secure random value

4. Use HTTPS exclusively

5. Configure proper CORS origins for your frontend domain

6. Set `ASPNETCORE_ENVIRONMENT=Production`

## Troubleshooting

### MongoDB Connection Error

- Verify `MONGO_USER`, `MONGO_PASSWORD`, and `MONGO_CLUSTER` are correct
- Ensure MongoDB Atlas network access is properly configured
- Check if your IP address is in the IP whitelist

### JWT Token Issues

- Ensure `JWT_KEY` is set and has sufficient length (minimum 32 characters)
- Verify token is being sent in Authorization header as "Bearer {token}"
- Check token expiration

### CORS Errors

- Verify `CORS_ORIGIN` matches your frontend URL
- Check that the frontend is making requests to the correct API endpoint

## Contributing

When contributing:

1. Create a new branch for your feature
2. Follow the existing code structure and naming conventions
3. Add appropriate comments for complex logic
4. Test your changes locally
5. Update this README if adding new features or endpoints

## Security

This project includes both secure and intentionally insecure versions to demonstrate security best practices and vulnerabilities. The secure version (WebAppBookLibrary) implements:

- Password hashing using industry-standard algorithms
- JWT token validation
- Input validation and sanitization
- Email format validation
- CORS protection
- Secure password requirements

## License

[Specify your license here]

## Contact

For questions or support, contact the project maintainer.

## Version History

- v1.0.0 - Initial release

